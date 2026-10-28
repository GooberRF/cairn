using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;

namespace Cairn.Lvl.Conversion;

/// <summary>
/// Levels and group files to editor group files (.rfg). A level's brushes go over whole (the editor needs every face,
/// portals and invisible faces included), with their flags, life, state, textures, scrolling and Alpine data; its
/// groups and moving groups keep their members and keyframes; objects follow the object filter. The compiled
/// geometry can go instead, one detail brush per room. What a group file has no place for is listed in the report.
/// </summary>
public static partial class LvlConverter
{
    static partial void ConvertToRfg(LvlConvertInput input, LvlConvertOptions options, CancellationToken cancellationToken, ref LvlConversion? conversion)
    {
        conversion = input.GroupFile is { } file
            ? GroupFileToRfg(file, input.Name, options, cancellationToken)
            : LevelToRfg(input.Level ?? throw new ArgumentException("The input holds neither a level nor a group file.", nameof(input)), input.Name, options, cancellationToken);
    }

    private static string RfgName(string source) => Path.GetFileNameWithoutExtension(source) + ".rfg";

    // ── Group files ────────────────────────────────────────────────────────

    private static LvlConversion GroupFileToRfg(RfGroupFile file, string name, LvlConvertOptions options, CancellationToken ct)
    {
        var report = new LvlReportBuilder();
        if (options.Geometry == LvlGeometryChoice.Compiled)
            report.Info(LvlConvertCodes.GeometryChoice, "A group file has no compiled geometry; its brushes are converted.", LvlIssueKind.Approximated);
        if (file.Problems.Length > 0)
            report.Warning(LvlConvertCodes.SourceProblems, $"The group file was read with {LvlReportBuilder.Count(file.Problems.Length, "problem")}; what could be read is converted.", LvlIssueKind.Dropped);
        int filtered = 0;
        var groups = file.Groups.Select(g =>
        {
            ct.ThrowIfCancellationRequested();
            var kept = g.Objects.All().Where(o => Keeps(options, o, ref filtered)).ToList();
            return g with { Objects = RfgFileAssembler.ObjectSet(kept) with { LegacyWaypointLists = KeepWaypointLists(g.Objects, options) } };
        }).ToList();
        if (filtered > 0)
            report.Info(LvlConvertCodes.ObjectsDropped, $"{LvlReportBuilder.Count(filtered, "object")} left out by the object filter.", LvlIssueKind.Dropped);
        int brushes = groups.Sum(g => g.Brushes.Length);
        report.SourceFaces = groups.Sum(g => g.Brushes.Sum(b => b.Geometry.Faces.Length));
        if (brushes == 0 && groups.All(g => g.Objects.Count == 0))
        {
            report.Error(LvlConvertCodes.Empty, "The group file has nothing to convert.", LvlIssueKind.Dropped);
            return new LvlConversion(LvlConvertTarget.Rfg, report.Build(), null, null, null);
        }
        var output = file with { Name = RfgName(name), Version = RfgReader.AlpineVersion, Groups = [.. groups], Problems = [], UnknownDataOffset = null, Fallbacks = RfGroupFallbacks.None };
        if (file.Version != RfgReader.AlpineVersion)
            report.Info(LvlRfgCodes.Written, $"The group file (version {file.Version}) is written with version {RfgReader.AlpineVersion}, the one current Alpine RED writes.");
        return RfgOutput.Finish(output, RfgReader.AlpineVersion, report);
    }

    /// <summary>The old-style waypoint lists keep only the waypoints the filter keeps (they are nav points).</summary>
    private static ImmutableArray<RfWaypointList> KeepWaypointLists(RfObjectSet objects, LvlConvertOptions options) =>
        (options.Objects & LvlObjectKinds.NavPoints) != 0 ? objects.LegacyWaypointLists : [];

    private static bool Keeps(LvlConvertOptions options, RfObject o, ref int filtered)
    {
        // Old-style waypoints travel inside their lists.
        if (o is RfLegacyWaypoint) return false;
        if ((options.Objects & LvlScene.KindOf(o)) != 0) return true;
        filtered++;
        return false;
    }

    // ── Levels ─────────────────────────────────────────────────────────────

    private static LvlConversion LevelToRfg(RfLevel level, string name, LvlConvertOptions options, CancellationToken ct)
    {
        var report = new LvlReportBuilder();
        if (level.Problems.Length > 0)
            report.Warning(LvlConvertCodes.SourceProblems, $"The level was read with {LvlReportBuilder.Count(level.Problems.Length, "problem")} (see the level's facts); what could be read is converted.", LvlIssueKind.Dropped);

        var asm = new RfgFileAssembler(Path.GetFileNameWithoutExtension(name), report);
        asm.Reserve(level.Brushes.Select(b => b.Uid).Concat(level.Movers.Select(b => b.Uid)).Concat(level.Objects.All().Select(o => o.Uid))
            .Concat(level.Groups.Concat(level.MovingGroups).SelectMany(g => g.Moving?.Keyframes.Select(k => k.Uid) ?? [])));

        // Groups: the editor's (moving ones included); old files only have the game's moving groups.
        var levelGroups = level.Groups.Length > 0 ? level.Groups : level.MovingGroups;
        var brushGroup = new Dictionary<int, int>();
        var objectGroup = new Dictionary<int, int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < levelGroups.Length; i++)
        {
            var g = levelGroups[i];
            string groupName = string.IsNullOrWhiteSpace(g.Name) ? $"group {i + 1}" : g.Name;
            for (int n = 2; !names.Add(groupName); n++) groupName = $"{g.Name} ({n})";
            int index = asm.Group(groupName, g.Moving);
            foreach (int uid in g.BrushUids) brushGroup.TryAdd(uid, index);
            foreach (int uid in g.ObjectUids) objectGroup.TryAdd(uid, index);
        }

        int lit = 0, rf2Flags = 0;
        RfBrush ForGroup(RfBrush b)
        {
            var g = b.Geometry;
            bool hasLightmaps = g.Surfaces.Length > 0 || g.Faces.Any(f => f.HasSurface || f.Room != -1 || f.PortalPlus2 != 0);
            if (hasLightmaps || g.Rooms.Length > 0 || g.Portals.Length > 0 || g.SubroomLists.Length > 0)
            {
                if (hasLightmaps) lit++;
                // Group brushes as RED writes them: no rooms, portals or lightmap placements; RED relights the level.
                g = g with
                {
                    Rooms = [],
                    SubroomLists = [],
                    Portals = [],
                    Surfaces = [],
                    Faces = [.. g.Faces.Select(f => f with { Surface = -1, Room = -1, PortalPlus2 = 0, Corners = [.. f.Corners.Select(c => c with { LightmapUv = null })] })],
                };
            }
            var flags = b.SolidFlags;
            if (flags != b.Rf1SolidFlags)
            {
                rf2Flags++;
                flags = b.Rf1SolidFlags;
            }
            // RED saves its selection as the brush state (3); an imported group should not arrive selected.
            return b with { Geometry = g, SolidFlags = flags, Rf2Extra = [], State = RfgBrushBuilder.ImportState(b.State) };
        }

        var geometry = options.ResolvedGeometry;
        // Compiled rooms are already the result of RED's build: as solid brushes they would be built again, so they
        // are always detail brushes.
        var builder = new RfgBrushBuilder(RfgBrushKind.Detail, report);
        int brushesFromMovers = 0;
        report.SourceFaces = geometry == LvlGeometryChoice.Compiled ? level.StaticGeometry.Faces.Length : level.Brushes.Sum(b => b.Geometry.Faces.Length);
        if (geometry == LvlGeometryChoice.Compiled)
        {
            AddRooms(level, options, asm, builder, report, ct);
            // Movers are compiled per mover; their uids are the editor brushes' uids, so their groups find them.
            foreach (var mover in level.Movers)
                asm.AddBrush(brushGroup.GetValueOrDefault(mover.Uid, 0), ForGroup(mover) with { Uid = asm.Uid(mover.Uid) });
        }
        else
        {
            var seen = new HashSet<int>();
            foreach (var brush in level.Brushes)
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(brush.Uid);
                asm.AddBrush(brushGroup.GetValueOrDefault(brush.Uid, 0), ForGroup(brush) with { Uid = asm.Uid(brush.Uid) });
            }
            // A moving group's brush that only the game's mover list holds (files without editor brushes).
            foreach (var mover in level.Movers)
            {
                if (seen.Contains(mover.Uid) || !brushGroup.TryGetValue(mover.Uid, out int g)) continue;
                asm.AddBrush(g, ForGroup(mover) with { Uid = asm.Uid(mover.Uid) });
                brushesFromMovers++;
            }
            if (options.ExcludedFaceClasses != RfFaceClass.Normal || !options.IncludeNormalFaces)
                report.Info(LvlConvertCodes.FilteredFaces, "Brushes are written whole (the editor needs every face, portal and invisible faces included); the face filter applies to compiled geometry only.");
        }

        int filtered = 0;
        foreach (var obj in level.Objects.All())
        {
            if (!Keeps(options, obj, ref filtered)) continue;
            asm.AddObject(objectGroup.GetValueOrDefault(obj.Uid, 0), obj with { Uid = asm.Uid(obj.Uid) });
        }
        if (filtered > 0)
            report.Info(LvlConvertCodes.ObjectsDropped, $"{LvlReportBuilder.Count(filtered, "object")} left out by the object filter.", LvlIssueKind.Dropped);
        if ((options.Objects & LvlObjectKinds.Keyframes) == 0 && levelGroups.Any(g => g.IsMoving))
            report.Info(LvlConvertCodes.ObjectsDropped, "Moving groups keep their keyframes although the object filter leaves keyframes out: a moving group cannot move without them.");

        if (lit > 0)
            report.Info(LvlRfgCodes.Lightmaps, $"{LvlReportBuilder.Count(lit, "brush", "brushes")} had lightmap placements; group brushes carry none, and RED relights the level when it is built.", LvlIssueKind.Dropped);
        if (rf2Flags > 0)
            report.Info(LvlConvertCodes.BrushData, $"{LvlReportBuilder.Count(rf2Flags, "brush", "brushes")} had solid flags RED does not keep; only portal, air, detail and emits steam were written.", LvlIssueKind.Dropped);
        if (brushesFromMovers > 0)
            report.Info(LvlConvertCodes.BrushData, $"{LvlReportBuilder.Count(brushesFromMovers, "mover brush", "mover brushes")} came from the game's mover list (the level has no editor copy of them).", LvlIssueKind.Approximated);
        NotInGroups(level, geometry != LvlGeometryChoice.Compiled, report);
        builder.Summarise();

        var file = asm.Build(RfgName(name), RfgReader.AlpineVersion);
        file = file with { OpaqueChunks = AlpineChunks(level, report) };
        if (file.Groups.All(g => g.Brushes.Length == 0 && g.Objects.Count == 0 && g.Moving is null) && file.OpaqueChunks.Length == 0)
        {
            report.Error(LvlConvertCodes.Empty, "The level has nothing a group file can hold.", LvlIssueKind.Dropped);
            return new LvlConversion(LvlConvertTarget.Rfg, report.Build(), null, null, null);
        }
        return RfgOutput.Finish(file, RfgReader.AlpineVersion, report);
    }

    /// <summary>Compiled geometry: one brush per room (faces chosen by the face filter), placed at the room's centre.</summary>
    private static void AddRooms(RfLevel level, LvlConvertOptions options, RfgFileAssembler asm, RfgBrushBuilder builder, LvlReportBuilder report, CancellationToken ct)
    {
        var g = level.StaticGeometry;
        var rooms = new SortedDictionary<int, List<RfFace>>();
        int filtered = 0;
        foreach (var f in g.Faces)
        {
            if (f.Corners.Length < 3 || f.Corners.Any(c => (uint)c.Vertex >= (uint)g.Vertices.Length)) continue;
            if (!options.Keeps(RfFaceClasses.OfCompiled(f)))
            {
                filtered++;
                continue;
            }
            int room = f.Room >= 0 && f.Room < g.Rooms.Length ? f.Room : -1;
            if (!rooms.TryGetValue(room, out var list)) rooms[room] = list = [];
            list.Add(f);
        }
        if (filtered > 0)
            report.Info(LvlConvertCodes.FilteredFaces, $"The face filter left out {LvlReportBuilder.Count(filtered, "face")} of the compiled geometry.", LvlIssueKind.Dropped);
        foreach (var (room, faces) in rooms)
        {
            ct.ThrowIfCancellationRequested();
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var f in faces)
                foreach (var c in f.Corners)
                {
                    min = Vector3.Min(min, g.Vertices[c.Vertex]);
                    max = Vector3.Max(max, g.Vertices[c.Vertex]);
                }
            var center = (min + max) / 2;
            var source = new RfgSourceBrush { Name = room >= 0 ? $"room {room}" : "faces outside rooms", Position = center };
            foreach (var f in faces)
            {
                source.Faces.Add(new RfgSourceFace([.. f.Corners.Select(c => new RfgSourceCorner(g.Vertices[c.Vertex] - center, c.Uv))], g.TextureOf(f) ?? "")
                {
                    Flags = f.Flags,
                    RawFlags = f.RawFlags,
                    Smoothing = f.Smoothing,
                    Scroll = f.Scroll,
                    FaceId = f.FaceId,
                });
            }
            if (builder.Build(source, asm.Uid(null)) is { } brush) asm.AddBrush(0, brush);
        }
        report.Info(LvlConvertCodes.GeometryChoice, $"The compiled geometry became {LvlReportBuilder.Count(rooms.Count, "brush", "brushes")}, one per room. They are detail brushes (the compiled geometry is already what RED's build made, so it must not be built again); for editing the level, convert its brushes instead.", LvlIssueKind.Approximated);
    }

    // Alpine object chunks a group file carries as a level stores them: notes, bags, weather regions, vehicle
    // factories, projection cameras and rope emitters (read the same whatever the file version), and directional
    // lights and terrain (read with the newest layout, so only from a level of that version).
    private static readonly uint[] PlainAlpineChunks = [0x0AFBAE02, 0x0AFBAE04, 0x0AFBAE06, 0x0AFBAE07, 0x0AFBAE08, 0x0AFBAE0A];
    private static readonly uint[] VersionedAlpineChunks = [0x0AFBAE0C, 0x0AFBAE0B];
    private const int NewestAlpineLevelVersion = 306;
    private const int MaxGroupChunkBytes = 10_000_000;

    /// <summary>The level's Alpine object chunks that a group file can carry (their objects join the first group on import).</summary>
    private static ImmutableArray<RfOpaqueSection> AlpineChunks(RfLevel level, LvlReportBuilder report)
    {
        var damaged = level.Sections.Where(s => s.Status is RfSectionStatus.Damaged or RfSectionStatus.Truncated or RfSectionStatus.Unknown).Select(s => s.Offset).ToHashSet();
        var kept = new List<RfOpaqueSection>();
        var dropped = new List<string>();
        foreach (var chunk in level.OpaqueSections)
        {
            bool plain = PlainAlpineChunks.Contains(chunk.Id);
            bool versioned = VersionedAlpineChunks.Contains(chunk.Id);
            if (!plain && !versioned) continue;
            if (damaged.Contains(chunk.Offset)) dropped.Add($"{chunk.Name} (damaged)");
            else if (versioned && level.Version < NewestAlpineLevelVersion) dropped.Add($"{chunk.Name} (stored in an older layout than group files use)");
            else if (chunk.Body.Length > MaxGroupChunkBytes) dropped.Add($"{chunk.Name} (larger than Alpine reads from a group file)");
            else kept.Add(chunk);
        }
        if (kept.Count > 0)
            report.Info(LvlConvertCodes.Objects, $"Alpine objects kept as stored: {string.Join(", ", kept.Select(c => c.Name).Distinct())}. Alpine RED puts them in the first group when it imports the file.");
        if (dropped.Count > 0)
            report.Warning(LvlRfgCodes.AlpineData, $"Alpine objects a group file cannot carry from this level were left out: {string.Join(", ", dropped)}.", LvlIssueKind.Dropped);
        return [.. kept];
    }

    /// <summary>Lists what a level holds that a group file has no place for.</summary>
    private static void NotInGroups(RfLevel level, bool brushes, LvlReportBuilder report)
    {
        var dropped = new List<string>();
        if (level.PlayerStart is not null) dropped.Add("the player start");
        if (level.Cutscenes.Length > 0) dropped.Add(LvlReportBuilder.Count(level.Cutscenes.Length, "cutscene"));
        if (level.CutscenePaths.Length > 0) dropped.Add(LvlReportBuilder.Count(level.CutscenePaths.Length, "cutscene path"));
        if (level.WaypointLists.Length > 0) dropped.Add(LvlReportBuilder.Count(level.WaypointLists.Length, "waypoint list"));
        int connected = level.Objects.NavPoints.Count(n => n.Connections.Length > 0);
        if (connected > 0) dropped.Add($"the connections of {LvlReportBuilder.Count(connected, "nav point")}");
        if (level.EditorLights.Length > 0) dropped.Add(LvlReportBuilder.Count(level.EditorLights.Length, "editor-only light"));
        if (level.Properties is not null || level.Info is not null) dropped.Add("the level properties and info (ambient light, fog, geomod texture, author)");
        if (level.AlpineProperties is { } alpine)
            dropped.Add(alpine.HoldOpenKeyframes.Length > 0 ? "the Alpine level properties (with the movers' hold-open settings)" : "the Alpine level properties");
        if (level.Preloads.Length > 0) dropped.Add("the preload lists");
        if (dropped.Count > 0)
            report.Info(LvlRfgCodes.NotInGroups, $"A group file has no place for {string.Join(", ", dropped)}; they are not written.", LvlIssueKind.Dropped);
        if (brushes && level.Brushes.Length > 0)
            report.Info(LvlConvertCodes.GeometryChoice, "RED builds the level geometry from the brushes; the compiled geometry is not part of a group file.");
    }
}
