using System.Buffers.Binary;
using Cairn.Formats;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Reading;

/// <summary>
/// Reads a Red Faction editor group file (.rfg): every group with its brushes and objects, then the Alpine chunks.
/// Group files have no section lengths, so every record must be understood to reach the next list; each field is
/// gated by the file version as RED reads it, and a count at the very end of the file means an empty list (old
/// Volition groups end early). A file whose walk fails is retried with the layout fallbacks real files need
/// (<see cref="RfGroupFallbacks"/>); if none fits, it throws <see cref="AssetFormatException"/>.
/// </summary>
public static class RfgReader
{
    /// <summary>The group file signature (bytes 0D D0 3D D4).</summary>
    public const uint Magic = 0xD43DD00D;

    /// <summary>The version current Alpine RED writes (and Cairn writes).</summary>
    public const int AlpineVersion = 300;

    /// <summary>Alpine chunk lengths above this are refused (Alpine's own cap).</summary>
    private const int MaxChunkBytes = 10_000_000;

    /// <summary>Reads a group file.</summary>
    /// <exception cref="AssetFormatException">Not a group file, unsupported version, or a layout no known writer produces.</exception>
    public static RfGroupFile ReadFile(string path) => Read(LvlFiles.ReadAll(path), Path.GetFileName(path));

    /// <summary>Reads a group file held in memory. The bytes must not change while the result is in use (opaque chunks refer to them).</summary>
    /// <exception cref="AssetFormatException">Not a group file, unsupported version, or a layout no known writer produces.</exception>
    public static RfGroupFile Read(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) throw new AssetFormatException($"'{name}' is empty.");
        if (data.Length < 8) throw new AssetFormatException($"'{name}' is too short to be a group file ({data.Length} bytes).");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic != Magic) throw new AssetFormatException($"'{name}' is not a Red Faction group file (signature 0x{magic:X8}).");
        int v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        if (v < RflLevelReader.OldestVersion)
            throw new AssetFormatException($"'{name}' has group version {v}, older than any group Cairn reads (the oldest is {RflLevelReader.OldestVersion}).");

        var rules = LvlRules.For(v, groupFile: true);
        // The plain rules first, then each fallback the corpus needs, in order of how often it is the answer.
        var attempts = new List<(LvlRules Rules, RfGroupFallbacks Fallback)> { (rules, RfGroupFallbacks.None) };
        attempts.Add(v < 300
            ? (rules with { AlpineEventMatrices = true }, RfGroupFallbacks.AlpineEventMatricesBelow300)
            : (rules with { AlpineEventMatrices = false }, RfGroupFallbacks.NoAlpineEventMatrices));
        if (v == 0xAE) attempts.Add((rules with { NoPlayerCollideAbsent = true }, RfGroupFallbacks.NoPlayerCollideAbsent));
        attempts.Add((rules with { ReduxEventMatrices = true }, RfGroupFallbacks.ReduxEventMatrices));

        string? firstError = null;
        foreach (var (attempt, fallback) in attempts)
        {
            try
            {
                return ReadWith(data, name, attempt, fallback);
            }
            catch (LvlBodyException e)
            {
                firstError ??= e.Message;
            }
        }
        throw new AssetFormatException($"'{name}' is damaged: {firstError}.");
    }

    private static RfGroupFile ReadWith(byte[] data, string name, LvlRules rules, RfGroupFallbacks fallback)
    {
        int v = rules.Version;
        var rr = new LvlRecordReader(rules);
        var c = new LvlCursor(data, 8, data.Length);
        int count = v >= 0x6C ? c.Count(3) : 1;
        var groups = new List<RfGroup>(count);
        for (int i = 0; i < count; i++)
        {
            try
            {
                groups.Add(Group(c, rr));
            }
            catch (LvlBodyException e)
            {
                throw new LvlBodyException($"group {i + 1} of {count}: {e.Message}");
            }
        }

        var problems = new List<string>();
        if (fallback == RfGroupFallbacks.ReduxEventMatrices)
            problems.Add("The file stores event orientations RED does not read (written by REDUX); RED cannot load it as it is.");
        if (rr.BadCorners > 0)
            problems.Add($"{rr.BadCorners} face corners name a vertex that does not exist; those faces cannot be drawn.");

        // Alpine chunks follow the last group until the end of the file.
        var opaque = new List<RfOpaqueSection>();
        var meshes = new List<RfAlpineMesh>();
        var coronas = new List<RfCorona>();
        RfBrushMetadata? metadata = null;
        long? unknownAt = null;
        while (c.Left > 0)
        {
            int at = c.Position;
            if (c.Left < 8)
            {
                unknownAt = at;
                problems.Add($"{c.Left} bytes after the groups at offset {at} are not an Alpine chunk; they were ignored.");
                break;
            }
            uint id = c.U32();
            int length = c.I32();
            if (length < 0 || length > MaxChunkBytes || length > c.Left || (id & 0xFFFFFF00) != 0x0AFBAE00 || id == 0x0AFBAE00 || id > 0x0AFBAE0C)
            {
                // Alpine RED stops at the first chunk it does not know, and so does Cairn.
                unknownAt = at;
                problems.Add($"Data after the groups at offset {at} (id 0x{id:X8}) is not an Alpine chunk this version knows; it was ignored.");
                break;
            }
            var body = new LvlCursor(data, c.Position, c.Position + length);
            c.Skip(length);
            string chunkName = RflLevelReader.SectionName(id);
            try
            {
                switch (id)
                {
                    case 0x0AFBAE01:
                        var result = rr.AlpineMeshes(body) ?? throw new LvlBodyException("no known Alpine mesh layout fits");
                        meshes.AddRange(result.Meshes);
                        if (result.Layout != RfAlpineMeshLayout.Full) problems.Add($"Alpine meshes use an older layout ({result.Layout}).");
                        continue;
                    case 0x0AFBAE03:
                        var list = rr.Coronas(body);
                        body.ExpectEnd("coronas");
                        coronas.AddRange(list);
                        continue;
                    case 0x0AFBAE05:
                        metadata = LvlRecordReader.BrushMetadata(body);
                        body.ExpectEnd("brush metadata");
                        continue;
                }
                opaque.Add(new RfOpaqueSection(id, chunkName, at, body.Memory(body.Left), $"{chunkName} are kept as stored"));
            }
            catch (LvlBodyException e)
            {
                body.Position = body.Start;
                opaque.Add(new RfOpaqueSection(id, chunkName, at, body.Memory(body.Left), e.Message));
                problems.Add($"{chunkName} at offset {at} does not match the expected layout ({e.Message}); it was kept as stored.");
            }
        }

        AttachAlpine(groups, meshes, coronas, metadata, problems);
        return new RfGroupFile
        {
            Name = name,
            Version = v,
            Groups = [.. groups],
            BrushMetadata = metadata,
            OpaqueChunks = [.. opaque],
            UnknownDataOffset = unknownAt,
            Fallbacks = fallback,
            Problems = [.. problems],
        };
    }

    /// <summary>One group record. A list count at the very end of the file means that list and all after it are empty.</summary>
    private static RfGroup Group(LvlCursor c, LvlRecordReader rr)
    {
        int v = rr.Rules.Version;
        string name = c.Str();
        bool moving = v >= 0x5B && c.Bool();
        var data = moving ? rr.MovingData(c) : null;
        var brushes = Optional(c, () => rr.Brushes(c), "brushes");

        ImmutableArray<T> L<T>(int minimumBytes, Func<LvlCursor, T> read, string what) =>
            Optional(c, () => LvlRecordReader.List(c, minimumBytes, read), what);

        var objects = new RfObjectSet
        {
            GeoRegions = L(8, rr.GeoRegion, "geo regions"),
            Lights = L(57, rr.Light, "lights"),
            CutsceneCameras = L(57, rr.CutsceneCamera, "cutscene cameras"),
            CutscenePathNodes = v >= 0x8C ? L(57, rr.CutscenePathNode, "cutscene path nodes") : [],
            AmbientSounds = L(17, rr.AmbientSound, "ambient sounds"),
            Events = L(30, rr.Event, "events"),
            RespawnPoints = L(54, rr.RespawnPoint, "respawn points"),
            LegacyWaypointLists = v < 0x8C ? L(6, LegacyWaypointList(rr), "waypoint lists") : [],
            NavPoints = L(30, rr.NavPoint, "nav points"),
            Entities = L(57, rr.Entity, "entities"),
            Items = L(69, rr.Item, "items"),
            Clutter = L(57, rr.Clutter, "clutter"),
            Triggers = L(30, rr.Trigger, "triggers"),
            ParticleEmitters = L(57, rr.ParticleEmitter, "particle emitters"),
            GasRegions = L(57, rr.GasRegion, "gas regions"),
            Decals = L(57, rr.Decal, "decals"),
            ClimbingRegions = L(57, rr.ClimbingRegion, "climbing regions"),
            RoomEffects = L(57, rr.RoomEffect, "room effects"),
            EaxEffects = v >= 0xB4 ? L(59, rr.EaxEffect, "EAX effects") : [],
            BoltEmitters = L(57, rr.BoltEmitter, "bolt emitters"),
            Targets = L(57, rr.Target, "targets"),
            PushRegions = L(57, rr.PushRegion, "push regions"),
        };
        return new RfGroup { Name = name, Moving = data, Brushes = brushes, Objects = objects };
    }

    private static Func<LvlCursor, RfWaypointList> LegacyWaypointList(LvlRecordReader rr) =>
        c => new RfWaypointList { Name = c.Str(), LegacyWaypoints = LvlRecordReader.List(c, 17, rr.LegacyWaypoint) };

    private static ImmutableArray<T> Optional<T>(LvlCursor c, Func<ImmutableArray<T>> read, string what)
    {
        // RED reads every count as 0 at the end of the file.
        if (c.AtEnd) return [];
        try
        {
            return read();
        }
        catch (LvlBodyException e)
        {
            throw new LvlBodyException($"{what}: {e.Message}");
        }
    }

    /// <summary>
    /// Alpine chunks carry no group membership: Alpine RED puts every Alpine object in the first group of the import
    /// and applies the brush metadata to that group's brushes by index. The model does the same.
    /// </summary>
    private static void AttachAlpine(List<RfGroup> groups, List<RfAlpineMesh> meshes, List<RfCorona> coronas,
        RfBrushMetadata? metadata, List<string> problems)
    {
        if (meshes.Count == 0 && coronas.Count == 0 && metadata is null) return;
        if (groups.Count == 0)
        {
            problems.Add("The file has Alpine objects but no group; they were put in a new unnamed group.");
            groups.Add(new RfGroup());
        }
        var g = groups[0];
        var objects = g.Objects with
        {
            AlpineMeshes = [.. g.Objects.AlpineMeshes, .. meshes],
            Coronas = [.. g.Objects.Coronas, .. coronas],
        };
        var brushes = g.Brushes;
        if (metadata is not null)
        {
            var b = brushes.ToBuilder();
            int outside = 0;
            foreach (var e in metadata.Entries)
            {
                if (e.BrushIndex < 0 || e.BrushIndex >= b.Count)
                {
                    outside++;
                    continue;
                }
                b[e.BrushIndex] = b[e.BrushIndex] with { Alpine = new RfAlpineBrushData(e.Flags, e.Material, e.ExtraBytes) };
            }
            if (outside > 0) problems.Add($"{outside} Alpine brush entries name a brush the first group does not have.");
            brushes = b.ToImmutable();
        }
        groups[0] = g with { Objects = objects, Brushes = brushes };
    }
}
