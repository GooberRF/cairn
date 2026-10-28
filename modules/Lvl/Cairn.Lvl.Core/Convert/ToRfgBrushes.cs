using Cairn.Formats.Obj;
using Cairn.Lvl.Model;
using Cairn.Lvl.Writing;

namespace Cairn.Lvl.Conversion;

/// <summary>What a mesh becomes when it is turned into a brush.</summary>
public enum RfgBrushKind
{
    /// <summary>
    /// A closed, outward-facing mesh becomes a solid brush; anything else becomes a detail brush (RED does not cut
    /// detail brushes with its level build, so an open mesh cannot leak), as Alpine's own mesh import does.
    /// </summary>
    Auto,
    /// <summary>Always a solid brush (an open mesh may leave holes in RED's build; the report warns).</summary>
    Solid,
    /// <summary>Always a detail brush.</summary>
    Detail,
}

/// <summary>Options for turning meshes (glTF, OBJ, V3M/V3C) into a group file.</summary>
public sealed record RfgImportOptions
{
    /// <summary>What meshes without stored brush flags become.</summary>
    public RfgBrushKind BrushKind { get; init; } = RfgBrushKind.Auto;

    /// <summary>V3M/V3C: the level of detail to convert (clamped to the LODs a submesh has).</summary>
    public int Lod { get; init; }

    /// <summary>glTF: the objects to carry (nodes Cairn wrote with their fields, and glTF lights).</summary>
    public LvlObjectKinds Objects { get; init; } = LvlObjectKinds.All;

    /// <summary>The name of the group that holds what belongs to no group; null uses the source's name.</summary>
    public string? GroupName { get; init; }

    /// <summary>The group file version written (300 is what current Alpine RED writes).</summary>
    public int Version { get; init; } = Reading.RfgReader.AlpineVersion;
}

/// <summary>Report codes of the conversions to .rfg (beside <see cref="LvlConvertCodes"/>).</summary>
public static class LvlRfgCodes
{
    /// <summary>What the group file holds, and what the writer had to change to fit its layout.</summary>
    public const string Written = "LC070";
    /// <summary>A mesh that is not closed (or faces inward) became a detail brush, or stays solid with a warning.</summary>
    public const string OpenMesh = "LC071";
    /// <summary>Faces with no area or repeated corners were dropped.</summary>
    public const string DegenerateFaces = "LC072";
    /// <summary>A brush with more triangles than RED's level build handles well.</summary>
    public const string LargeBrush = "LC073";
    /// <summary>Faces with more corners than RED keeps were split into triangles.</summary>
    public const string FacesSplit = "LC074";
    /// <summary>Level data a group file has no place for (player start, cutscenes, level properties ...).</summary>
    public const string NotInGroups = "LC075";
    /// <summary>Texture names taken from material names or image files.</summary>
    public const string TextureNames = "LC076";
    /// <summary>Uids that were missing or repeated and were given new ones.</summary>
    public const string Uids = "LC077";
    /// <summary>A node transform with scale, shear or a mirror was baked into the vertices.</summary>
    public const string TransformBaked = "LC078";
    /// <summary>Alpine data moved to the first group, or lost because it cannot live elsewhere.</summary>
    public const string AlpineData = "LC079";
    /// <summary>Face data (flags, smoothing groups) that had to be assumed.</summary>
    public const string FaceData = "LC081";
    /// <summary>Lightmap placements of level brushes are not carried (RED relights the level).</summary>
    public const string Lightmaps = "LC082";
    /// <summary>Double-sided faces were written twice, once each way.</summary>
    public const string DoubleSided = "LC083";
}

/// <summary>One corner of a mesh face on its way to a brush: position in the brush's own space, texture coordinates, and the source normal if any.</summary>
internal readonly record struct RfgSourceCorner(Vector3 Position, Vector2 Uv, Vector3? Normal = null);

/// <summary>One face of a mesh on its way to a brush, wound the RF way (Newell normal points out of the solid).</summary>
internal sealed record RfgSourceFace(RfgSourceCorner[] Corners, string Texture)
{
    public RfFaceFlags Flags { get; init; }
    /// <summary>The raw flags when the source kept them; null writes <see cref="Flags"/>.</summary>
    public uint? RawFlags { get; init; }
    /// <summary>Smoothing groups when the source kept them; null derives them from the corner normals.</summary>
    public uint? Smoothing { get; init; }
    public Vector2? Scroll { get; init; }
    public int FaceId { get; init; } = -1;
}

/// <summary>A mesh on its way to a brush: its faces in the brush's own space and the brush data the source kept.</summary>
internal sealed class RfgSourceBrush
{
    public required string Name { get; init; }
    public int? Uid { get; set; }
    public Vector3 Position { get; set; }
    public RfMatrix Orientation { get; set; } = RfMatrix.Identity;
    public List<RfgSourceFace> Faces { get; } = [];
    /// <summary>The solid flags the source stored (Cairn's own output); null lets the pre-flight decide.</summary>
    public RfSolidFlags? SolidFlags { get; set; }
    /// <summary>True when <see cref="SolidFlags"/> came from another tool and the closed-mesh test should still warn.</summary>
    public bool CheckStoredFlags { get; set; }
    public int Life { get; set; } = -1;
    public int State { get; set; }
    public RfAlpineBrushData? Alpine { get; set; }
    /// <summary>The group the brush belongs to, or null for the file's main group.</summary>
    public string? Group { get; set; }
}

/// <summary>
/// Turns meshes into brushes with the checks RED needs: positions welded by exact value into one vertex list, faces
/// without area or with repeated corners dropped, faces of more than 16 corners split, planes from the corners,
/// smoothing groups from the source normals when none were kept, and the closed-mesh test that decides between a
/// solid and a detail brush. Counts everything for the report.
/// </summary>
internal sealed class RfgBrushBuilder(RfgBrushKind kind, LvlReportBuilder report)
{
    /// <summary>RED splits faces with more corners than this.</summary>
    public const int MaxFaceCorners = 16;

    /// <summary>Alpine's mesh import warns above this many triangles per brush ("the CSG stage starts choking").</summary>
    public const int LargeBrushTriangles = 5000;

    private int _degenerate, _split, _openDetail, _inwardDetail, _openSolid, _closedSolid, _emptyBrushes, _derivedSmoothing, _untextured;
    private readonly List<string> _large = [];

    /// <summary>The brush, or null when no face is left.</summary>
    public RfBrush? Build(RfgSourceBrush source, int uid)
    {
        var vertices = new List<Vector3>();
        var index = new Dictionary<Vector3, int>();
        var textures = new List<string>();
        var textureIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var faces = new List<RfFace>();
        int triangles = 0;

        foreach (var face in source.Faces)
        {
            foreach (var piece in Pieces(face))
            {
                var corners = new List<RfCorner>(piece.Count);
                var points = new List<Vector3>(piece.Count);
                foreach (var c in piece)
                {
                    if (!Finite(c.Position) || !float.IsFinite(c.Uv.X) || !float.IsFinite(c.Uv.Y))
                    {
                        corners.Clear();
                        break;
                    }
                    if (!index.TryGetValue(c.Position, out int vi))
                    {
                        vi = vertices.Count;
                        vertices.Add(c.Position);
                        index[c.Position] = vi;
                    }
                    // A corner that repeats the one before it adds nothing (and RED cannot use it).
                    if (corners.Count > 0 && corners[^1].Vertex == vi) continue;
                    corners.Add(new RfCorner(vi, c.Uv));
                    points.Add(c.Position);
                }
                if (corners.Count > 1 && corners[0].Vertex == corners[^1].Vertex)
                {
                    corners.RemoveAt(corners.Count - 1);
                    points.RemoveAt(points.Count - 1);
                }
                var newell = Newell(points);
                if (corners.Count < 3 || !(newell.LengthSquared() > 1e-18f) || corners.Select(c => c.Vertex).Distinct().Count() != corners.Count)
                {
                    _degenerate++;
                    continue;
                }
                var normal = Vector3.Normalize(newell);
                int texture = -1;
                if (face.Texture.Length > 0)
                {
                    if (!textureIndex.TryGetValue(face.Texture, out texture))
                    {
                        texture = textures.Count;
                        textures.Add(face.Texture);
                        textureIndex[face.Texture] = texture;
                    }
                }
                else
                {
                    _untextured++;
                }
                uint smoothing = face.Smoothing ?? DeriveSmoothing(piece, normal);
                faces.Add(new RfFace
                {
                    Plane = new RfPlane(normal, -Vector3.Dot(normal, points[0])),
                    Texture = texture,
                    FaceId = face.FaceId,
                    Flags = face.Flags,
                    RawFlags = face.RawFlags ?? (uint)face.Flags,
                    Smoothing = smoothing,
                    Corners = [.. corners],
                    Scroll = face.Scroll,
                });
                triangles += corners.Count - 2;
            }
        }
        if (faces.Count == 0)
        {
            _emptyBrushes++;
            return null;
        }
        if (triangles > LargeBrushTriangles) _large.Add($"{source.Name} ({triangles:N0})");

        var geometry = new RfGeometry { Textures = [.. textures], Vertices = [.. vertices], Faces = [.. faces] };
        var flags = SolidFlagsFor(source, geometry);
        return new RfBrush
        {
            Uid = uid,
            Position = source.Position,
            Orientation = source.Orientation,
            Geometry = geometry,
            SolidFlags = flags,
            Life = source.Life,
            State = ImportState(source.State),
            Alpine = source.Alpine,
        };
    }

    /// <summary>The editor state a converted brush gets: RED's "selected" (3) becomes normal, others stay (2 is locked).</summary>
    public static int ImportState(int state) => state == 3 ? 0 : state;

    /// <summary>The face as RED can hold it: itself, or its triangles when it has more than 16 corners.</summary>
    private IEnumerable<IReadOnlyList<RfgSourceCorner>> Pieces(RfgSourceFace face)
    {
        if (face.Corners.Length <= MaxFaceCorners)
        {
            yield return face.Corners;
            yield break;
        }
        _split++;
        var points = face.Corners.Select(c => c.Position).ToArray();
        foreach (var (a, b, c) in ObjTriangulator.Triangulate(points))
            yield return [face.Corners[a], face.Corners[b], face.Corners[c]];
    }

    /// <summary>Smoothing group 1 for a face whose source normals bend away from its plane (a smooth surface), none for a flat one.</summary>
    private uint DeriveSmoothing(IReadOnlyList<RfgSourceCorner> corners, Vector3 normal)
    {
        bool any = false, bent = false;
        foreach (var c in corners)
        {
            if (c.Normal is not { } n || !Finite(n) || n.LengthSquared() < 1e-12f) continue;
            any = true;
            if (Vector3.Dot(Vector3.Normalize(n), normal) < 0.9995f) bent = true;
        }
        if (any) _derivedSmoothing++;
        return bent ? 1u : 0u;
    }

    private RfSolidFlags SolidFlagsFor(RfgSourceBrush source, RfGeometry g)
    {
        // Brush data a level kept (through Cairn's extras or comments) is written as it was: RED made those brushes,
        // and its own brushes often fail the strict test below (T-junctions after vertex edits, 45 of L1S1's 160).
        if (source.SolidFlags is { } stored && !source.CheckStoredFlags) return stored;
        var (closed, volume) = Closure(g);
        bool outward = closed && volume > 0;
        if (source.SolidFlags is { } named)
        {
            // Flags another tool kept (REDUX's object names). REDUX also writes a level's compiled geometry as one
            // "solid" brush: an open solid becomes a detail brush when the kind is automatic, else a warning.
            if ((named & (RfSolidFlags.Detail | RfSolidFlags.Portal)) != 0 || outward) return named;
            if (kind == RfgBrushKind.Auto && (named & RfSolidFlags.Air) == 0)
            {
                if (closed) _inwardDetail++;
                else _openDetail++;
                return named | RfSolidFlags.Detail;
            }
            _openSolid++;
            return named;
        }
        switch (kind)
        {
            case RfgBrushKind.Solid:
                if (!outward) _openSolid++;
                return RfSolidFlags.None;
            case RfgBrushKind.Detail:
                return RfSolidFlags.Detail;
            default:
                if (outward)
                {
                    _closedSolid++;
                    return RfSolidFlags.None;
                }
                if (closed) _inwardDetail++;
                else _openDetail++;
                return RfSolidFlags.Detail;
        }
    }

    /// <summary>
    /// Whether every edge is used exactly once in each direction (a closed, consistently wound surface), and the
    /// enclosed volume (positive when the faces point out).
    /// </summary>
    public static (bool Closed, double Volume) Closure(RfGeometry g)
    {
        var edges = new Dictionary<(int, int), int>();
        double volume = 0;
        foreach (var f in g.Faces)
        {
            var c = f.Corners;
            for (int k = 0; k < c.Length; k++)
            {
                var key = (c[k].Vertex, c[(k + 1) % c.Length].Vertex);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
            var p0 = g.Vertices[c[0].Vertex];
            for (int k = 1; k + 1 < c.Length; k++)
            {
                var p1 = g.Vertices[c[k].Vertex];
                var p2 = g.Vertices[c[k + 1].Vertex];
                volume += Vector3.Dot(p0, Vector3.Cross(p1, p2)) / 6.0;
            }
        }
        bool closed = edges.Count > 0;
        foreach (var (edge, count) in edges)
        {
            if (count != 1 || edges.GetValueOrDefault((edge.Item2, edge.Item1)) != 1)
            {
                closed = false;
                break;
            }
        }
        return (closed, volume);
    }

    public static Vector3 Newell(IReadOnlyList<Vector3> p)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            nx += (double)(a.Y - b.Y) * (a.Z + b.Z);
            ny += (double)(a.Z - b.Z) * (a.X + b.X);
            nz += (double)(a.X - b.X) * (a.Y + b.Y);
        }
        return new Vector3((float)nx, (float)ny, (float)nz);
    }

    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Adds the pre-flight findings to the report.</summary>
    public void Summarise()
    {
        static string N(int n, string one, string? many = null) => LvlReportBuilder.Count(n, one, many);
        if (_closedSolid > 0)
            report.Info(LvlRfgCodes.OpenMesh, $"Closed, outward-facing meshes became solid brushes: {N(_closedSolid, "mesh", "meshes")}.");
        if (_openDetail > 0)
            report.Info(LvlRfgCodes.OpenMesh, $"Meshes that are not closed (an edge not shared by exactly two faces) became detail brushes, which RED's level build does not cut, so they cannot leak: {N(_openDetail, "mesh", "meshes")}.", LvlIssueKind.Approximated);
        if (_inwardDetail > 0)
            report.Info(LvlRfgCodes.OpenMesh, $"Closed meshes that face inwards (like a room seen from inside) became detail brushes: {N(_inwardDetail, "mesh", "meshes")}.", LvlIssueKind.Approximated);
        if (_openSolid > 0)
            report.Warning(LvlRfgCodes.OpenMesh, $"Solid brushes that are not closed or face inwards, where RED's level build may leave holes or leaks (make them detail brushes in RED if it does): {N(_openSolid, "brush", "brushes")}.", LvlIssueKind.Approximated);
        if (_degenerate > 0)
            report.Info(LvlRfgCodes.DegenerateFaces, $"Faces with no area or a corner used twice were dropped (RED cannot use them): {N(_degenerate, "face")}.", LvlIssueKind.Dropped);
        if (_split > 0)
            report.Info(LvlRfgCodes.FacesSplit, $"Faces with more than {MaxFaceCorners} corners were split into triangles (RED keeps at most {MaxFaceCorners} corners per face): {N(_split, "face")}.", LvlIssueKind.Split);
        if (_large.Count > 0)
            report.Warning(LvlRfgCodes.LargeBrush, $"Brushes with more than {LargeBrushTriangles:N0} triangles, where RED's level build becomes very slow (detail brushes less so): {string.Join(", ", _large.Take(5))}{(_large.Count > 5 ? $" and {_large.Count - 5} more" : "")}.", LvlIssueKind.LimitBroken);
        if (_emptyBrushes > 0)
            report.Warning(LvlRfgCodes.DegenerateFaces, $"Meshes with no usable face left were not written: {N(_emptyBrushes, "mesh", "meshes")}.", LvlIssueKind.Dropped);
        if (_derivedSmoothing > 0)
            report.Info(LvlRfgCodes.FaceData, $"Smoothing groups of {N(_derivedSmoothing, "face")} come from the source normals: faces whose normals bend share one smoothing group, flat faces have none.", LvlIssueKind.Approximated);
        if (_untextured > 0)
            report.Warning(LvlRfgCodes.TextureNames, $"{N(_untextured, "face has", "faces have")} no texture; RED shows them with its default texture until one is applied.", LvlIssueKind.Approximated);
    }
}

/// <summary>
/// Collects brushes and objects into groups and makes the group file: the main group first (Alpine objects and
/// flagged brushes can only travel in it), then the named groups in first-seen order; every uid unique.
/// </summary>
internal sealed class RfgFileAssembler(string mainGroupName, LvlReportBuilder report)
{
    private sealed class Bucket
    {
        public required string Name { get; init; }
        public RfMovingData? Moving { get; set; }
        public List<RfBrush> Brushes { get; } = [];
        public List<RfObject> Objects { get; } = [];
    }

    private readonly List<Bucket> _groups = [new Bucket { Name = mainGroupName }];
    private readonly Dictionary<string, Bucket> _byName = new(StringComparer.Ordinal);
    private readonly HashSet<int> _uids = [];
    private int _nextUid = 1;
    private int _renumbered, _alpineMoved, _alpineLost;

    /// <summary>Reserves the uids the source already uses, so new ones never collide.</summary>
    public void Reserve(IEnumerable<int> uids)
    {
        foreach (int uid in uids)
            if (uid >= _nextUid && uid < int.MaxValue) _nextUid = uid + 1;
    }

    /// <summary>A uid for a brush or object: the wanted one when it is free, else a new one.</summary>
    public int Uid(int? wanted)
    {
        if (wanted is { } w && w >= 0 && _uids.Add(w)) return w;
        if (wanted is >= 0) _renumbered++;
        while (!_uids.Add(_nextUid)) _nextUid++;
        return _nextUid++;
    }

    /// <summary>The group with this name (made when first asked), or the main group for null.</summary>
    public int Group(string? name, RfMovingData? moving = null)
    {
        if (name is null) return 0;
        if (!_byName.TryGetValue(name, out var bucket))
        {
            bucket = new Bucket { Name = name };
            _byName[name] = bucket;
            _groups.Add(bucket);
        }
        bucket.Moving ??= moving;
        return _groups.IndexOf(bucket);
    }

    public void AddBrush(int group, RfBrush brush)
    {
        // Alpine brush flags are read for the first group's brushes only.
        if (group != 0 && brush.Alpine is { Flags: not RfAlpineBrushFlags.None })
        {
            if (_groups[group].Moving is null)
            {
                _alpineMoved++;
                group = 0;
            }
            else
            {
                _alpineLost++;
                brush = brush with { Alpine = null };
            }
        }
        _groups[group].Brushes.Add(brush);
    }

    public void AddObject(int group, RfObject obj)
    {
        // Alpine objects carry no group: Alpine RED puts them in the first group of the import.
        if (group != 0 && obj is RfAlpineMesh or RfCorona) group = 0;
        _groups[group].Objects.Add(obj);
    }

    public int BrushCount => _groups.Sum(g => g.Brushes.Count);

    public int ObjectCount => _groups.Sum(g => g.Objects.Count);

    public RfGroupFile Build(string name, int version)
    {
        var groups = new List<RfGroup>();
        for (int i = 0; i < _groups.Count; i++)
        {
            var b = _groups[i];
            if (i > 0 && b.Brushes.Count == 0 && b.Objects.Count == 0 && b.Moving is null) continue;
            groups.Add(new RfGroup { Name = b.Name, Moving = b.Moving, Brushes = [.. b.Brushes], Objects = ObjectSet(b.Objects) });
        }
        if (groups.Count > 1 && groups[0].Brushes.Length == 0 && groups[0].Objects.Count == 0) groups.RemoveAt(0);
        if (_renumbered > 0)
            report.Info(LvlRfgCodes.Uids, $"{LvlReportBuilder.Count(_renumbered, "uid")} were in use twice and were given new values (RED renumbers on import anyway).", LvlIssueKind.Approximated);
        if (_alpineMoved > 0)
            report.Info(LvlRfgCodes.AlpineData, $"{LvlReportBuilder.Count(_alpineMoved, "brush", "brushes")} with Alpine flags (geoable, breakable, no shadow) moved to the first group: Alpine reads those flags for the first group's brushes only.", LvlIssueKind.Approximated);
        if (_alpineLost > 0)
            report.Warning(LvlRfgCodes.AlpineData, $"{LvlReportBuilder.Count(_alpineLost, "brush", "brushes")} of moving groups lost their Alpine flags: Alpine reads them for the first group's brushes only. Set them again in RED.", LvlIssueKind.Dropped);
        return new RfGroupFile { Name = name, Version = version, Groups = [.. groups] };
    }

    /// <summary>Objects sorted into their lists, in the order they were added.</summary>
    public static RfObjectSet ObjectSet(IEnumerable<RfObject> objects)
    {
        var list = objects.ToList();
        ImmutableArray<T> Of<T>() => [.. list.OfType<T>()];
        return new RfObjectSet
        {
            GeoRegions = Of<RfGeoRegion>(),
            Lights = Of<RfLight>(),
            CutsceneCameras = Of<RfCutsceneCamera>(),
            CutscenePathNodes = Of<RfCutscenePathNode>(),
            AmbientSounds = Of<RfAmbientSound>(),
            Events = Of<RfEvent>(),
            RespawnPoints = Of<RfRespawnPoint>(),
            NavPoints = Of<RfNavPoint>(),
            Entities = Of<RfEntity>(),
            Items = Of<RfItem>(),
            Clutter = Of<RfClutter>(),
            Triggers = Of<RfTrigger>(),
            ParticleEmitters = Of<RfParticleEmitter>(),
            GasRegions = Of<RfGasRegion>(),
            Decals = Of<RfDecal>(),
            ClimbingRegions = Of<RfClimbingRegion>(),
            RoomEffects = Of<RfRoomEffect>(),
            EaxEffects = Of<RfEaxEffect>(),
            BoltEmitters = Of<RfBoltEmitter>(),
            Targets = Of<RfTarget>(),
            PushRegions = Of<RfPushRegion>(),
            AlpineMeshes = Of<RfAlpineMesh>(),
            Coronas = Of<RfCorona>(),
        };
    }
}

/// <summary>The group file a conversion made: written to bytes up front, so the report holds the writer's notes before anything touches the disk.</summary>
internal static class RfgOutput
{
    public static LvlConversion Finish(RfGroupFile file, int version, LvlReportBuilder report)
    {
        RfgWriteResult written;
        try
        {
            written = RfgWriter.Write(file, new RfgWriteOptions { Version = version });
        }
        catch (InvalidOperationException e)
        {
            report.Error(LvlConvertCodes.TooLarge, e.Message);
            return new LvlConversion(LvlConvertTarget.Rfg, report.Build(), null, null, null);
        }
        foreach (string note in written.Notes) report.Warning(LvlRfgCodes.Written, note, LvlIssueKind.Dropped);
        int brushes = file.Groups.Sum(g => g.Brushes.Length);
        int objects = file.Groups.Sum(g => g.Objects.Count + (g.Moving?.Keyframes.Length ?? 0));
        int moving = file.Groups.Count(g => g.IsMoving);
        report.Parts = brushes;
        report.Objects = objects;
        report.Faces = file.Groups.Sum(g => g.Brushes.Sum(b => b.Geometry.Faces.Length));
        report.Vertices = file.Groups.Sum(g => g.Brushes.Sum(b => b.Geometry.Vertices.Length));
        report.Triangles = (int)Math.Min(int.MaxValue, file.Groups.Sum(g => g.Brushes.Sum(b => b.Geometry.Faces.Sum(f => (long)Math.Max(0, f.Corners.Length - 2)))));
        report.Info(LvlRfgCodes.Written, $"Group file version {version}: {LvlReportBuilder.Count(file.Groups.Length, "group")} ({moving} moving), {LvlReportBuilder.Count(brushes, "brush", "brushes")}, {LvlReportBuilder.Count(objects, "object")}. RED gives imported objects new uids where the level already uses theirs.");
        var bytes = written.Bytes;
        return new LvlConversion(LvlConvertTarget.Rfg, report.Build(), file,
            path =>
            {
                string full = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
                Workspace.AtomicFile.WriteAllBytes(full, bytes);
                return [full];
            },
            path => [Path.GetFullPath(path)]);
    }
}
