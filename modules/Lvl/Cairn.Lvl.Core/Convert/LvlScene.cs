using Cairn.Lvl.Model;

namespace Cairn.Lvl.Conversion;

/// <summary>What a conversion reads: a level or a group file.</summary>
public sealed record LvlConvertInput
{
    /// <summary>The source's file name (used for output names and the report).</summary>
    public required string Name { get; init; }
    public RfLevel? Level { get; init; }
    public RfGroupFile? GroupFile { get; init; }

    public static LvlConvertInput FromLevel(RfLevel level) => new() { Name = level.Name, Level = level };

    public static LvlConvertInput FromGroupFile(RfGroupFile file) => new() { Name = file.Name, GroupFile = file };
}

internal enum LvlPartKind
{
    Brush,
    Room,
    Mover,
}

/// <summary>One corner as it is written: position and normal in the part's output space (RF axes), the corner's own UVs.</summary>
internal readonly record struct LvlCorner(Vector3 Position, Vector3 Normal, Vector2 Uv, Vector2? LightmapUv);

/// <summary>A face ready for output.</summary>
internal sealed record LvlPolygon(RfFace Face, int TextureSlot, string Texture, RfFaceClass Classes, Vector3 Normal, LvlCorner[] Corners);

/// <summary>A brush, a room of the compiled geometry or a mover: one glTF node, OBJ object or V3M submesh.</summary>
internal sealed class LvlPart
{
    public LvlPartKind Kind { get; init; }
    public required string Name { get; init; }
    /// <summary>The brush uid, or a generated one for rooms (glTF tools key brushes by it).</summary>
    public int Uid { get; init; }
    public RfBrush? Brush { get; init; }
    public int RoomIndex { get; init; } = -1;
    public RfRoom? Room { get; init; }
    public required RfGeometry Geometry { get; init; }
    /// <summary>True when positions are in world space; false when they are in the brush's own space.</summary>
    public bool TransformApplied { get; init; }
    public Vector3 Position { get; init; }
    public RfMatrix Orientation { get; init; } = RfMatrix.Identity;
    public List<LvlPolygon> Polygons { get; } = [];
    /// <summary>Index into <see cref="LvlScene.Groups"/>, or -1.</summary>
    public int Group { get; set; } = -1;
}

/// <summary>A group (or moving group) of the source.</summary>
internal sealed class LvlSceneGroup
{
    public required string Name { get; init; }
    public RfMovingData? Moving { get; init; }
    /// <summary>Levels: the member uids the group lists (objects and brushes).</summary>
    public ImmutableArray<int> ObjectUids { get; init; } = [];
    public ImmutableArray<int> BrushUids { get; init; } = [];
}

/// <summary>An object to write: the model record (an <see cref="RfObject"/> or the <see cref="RfPlayerStart"/>) and its <c>rf_type</c> name.</summary>
internal sealed record LvlSceneObject(object Record, string TypeName, int Uid, Vector3 Position, RfMatrix? Orientation, bool EditorOnly, int Group, int KeyframeIndex = -1);

/// <summary>
/// The source turned into parts, groups and objects with every shared conversion rule applied: faces filtered by
/// their classes (never by raw bits), brush transforms applied where asked, per-corner UVs, and normals from the
/// smoothing groups (faces that share a bit at a position average their area-weighted normals; others keep their own).
/// </summary>
internal sealed class LvlScene
{
    public required string Name { get; init; }
    public RfLevel? Level { get; init; }
    public RfGroupFile? GroupFile { get; init; }
    public LvlGeometryChoice Geometry { get; init; }
    public List<LvlPart> Parts { get; } = [];
    public List<LvlSceneGroup> Groups { get; } = [];
    public List<LvlSceneObject> Objects { get; } = [];
    /// <summary>Objects the kinds filter left out.</summary>
    public int ObjectsFiltered { get; set; }

    private readonly Dictionary<RfFaceClass, int> _filtered = [];
    private int _badIndex, _nonFinite, _short, _degenerate, _smoothed, _sourceFaces, _sourceVertices, _notRotations, _coloredCorners;

    public int SourceVertices => _sourceVertices;
    public int SourceFaces => _sourceFaces;

    /// <summary>Builds the scene. <paramref name="applyTransforms"/> false keeps brush geometry in its own space where the orientation is a rotation.</summary>
    public static LvlScene Build(LvlConvertInput input, LvlConvertOptions options, bool applyTransforms, LvlReportBuilder report, CancellationToken ct)
    {
        var geometry = input.GroupFile is not null ? LvlGeometryChoice.Brushes : options.ResolvedGeometry;
        if (input.GroupFile is not null && options.Geometry == LvlGeometryChoice.Compiled)
            report.Info(LvlConvertCodes.GeometryChoice, "A group file has no compiled geometry; its brushes are converted.", LvlIssueKind.Approximated);
        var scene = new LvlScene { Name = input.Name, Level = input.Level, GroupFile = input.GroupFile, Geometry = geometry };
        if (input.Level is { } level) scene.BuildLevel(level, options, applyTransforms, ct);
        else if (input.GroupFile is { } file) scene.BuildGroupFile(file, options, applyTransforms, ct);
        else throw new ArgumentException("The input holds neither a level nor a group file.", nameof(input));
        scene.Summarise(report);
        return scene;
    }

    // ── Levels ─────────────────────────────────────────────────────────────

    private void BuildLevel(RfLevel level, LvlConvertOptions options, bool apply, CancellationToken ct)
    {
        // Groups: the editor groups (moving ones included); older files only have the game's moving groups.
        var groups = level.Groups.Length > 0 ? level.Groups : level.MovingGroups;
        var brushGroup = new Dictionary<int, int>();
        var objectGroup = new Dictionary<int, int>();
        foreach (var g in groups)
        {
            int index = Groups.Count;
            Groups.Add(new LvlSceneGroup { Name = g.Name, Moving = g.Moving, ObjectUids = g.ObjectUids, BrushUids = g.BrushUids });
            foreach (int uid in g.BrushUids) brushGroup.TryAdd(uid, index);
            foreach (int uid in g.ObjectUids) objectGroup.TryAdd(uid, index);
        }

        int nextUid = 1 + Math.Max(0, level.Brushes.Concat(level.Movers).Select(b => b.Uid).DefaultIfEmpty(0).Max());
        if (Geometry == LvlGeometryChoice.Compiled)
        {
            var g = level.StaticGeometry;
            var rooms = new Dictionary<int, LvlPart>();
            AddSolid(g, null, RfGeometrySource.Compiled, apply: true, options, face =>
            {
                int room = face.Room >= 0 && face.Room < g.Rooms.Length ? face.Room : -1;
                if (!rooms.TryGetValue(room, out var part))
                {
                    part = new LvlPart
                    {
                        Kind = LvlPartKind.Room,
                        Name = room >= 0 ? $"room_{room}" : "room_none",
                        Uid = nextUid + (room + 1),
                        RoomIndex = room,
                        Room = room >= 0 ? g.Rooms[room] : null,
                        Geometry = g,
                        TransformApplied = true,
                    };
                    rooms[room] = part;
                }
                return part;
            }, ct);
            foreach (var part in rooms.OrderBy(kv => kv.Key < 0 ? int.MaxValue : kv.Key).Select(kv => kv.Value))
                if (part.Polygons.Count > 0) Parts.Add(part);
            if (options.IncludeMovers)
            {
                foreach (var mover in level.Movers) AddBrush(mover, LvlPartKind.Mover, RfGeometrySource.Compiled, apply, options, brushGroup, ct);
            }
        }
        else
        {
            foreach (var brush in level.Brushes) AddBrush(brush, LvlPartKind.Brush, RfGeometrySource.Brush, apply, options, brushGroup, ct);
        }

        foreach (var obj in level.Objects.All()) AddObject(obj, options, objectGroup.GetValueOrDefault(obj.Uid, -1), editorOnly: false);
        foreach (var light in level.EditorLights) AddObject(light, options, objectGroup.GetValueOrDefault(light.Uid, -1), editorOnly: true);
        if (level.PlayerStart is { } start)
        {
            if ((options.Objects & LvlObjectKinds.Spawns) != 0) Objects.Add(new LvlSceneObject(start, "player_start", -1, start.Position, start.Orientation, false, -1));
            else ObjectsFiltered++;
        }
        AddKeyframes(options);
    }

    // ── Group files ────────────────────────────────────────────────────────

    private void BuildGroupFile(RfGroupFile file, LvlConvertOptions options, bool apply, CancellationToken ct)
    {
        foreach (var g in file.Groups)
        {
            int index = Groups.Count;
            Groups.Add(new LvlSceneGroup { Name = g.Name, Moving = g.Moving });
            foreach (var brush in g.Brushes)
            {
                var part = AddBrush(brush, LvlPartKind.Brush, RfGeometrySource.Brush, apply, options, null, ct);
                if (part is not null) part.Group = index;
            }
            foreach (var obj in g.Objects.All()) AddObject(obj, options, index, editorOnly: false);
        }
        AddKeyframes(options);
    }

    private void AddKeyframes(LvlConvertOptions options)
    {
        for (int gi = 0; gi < Groups.Count; gi++)
        {
            if (Groups[gi].Moving is not { } moving) continue;
            for (int k = 0; k < moving.Keyframes.Length; k++)
            {
                var kf = moving.Keyframes[k];
                if ((options.Objects & LvlObjectKinds.Keyframes) == 0)
                {
                    ObjectsFiltered++;
                    continue;
                }
                Objects.Add(new LvlSceneObject(kf, "keyframe", kf.Uid, kf.Position, kf.Orientation, false, gi, k));
            }
        }
    }

    private void AddObject(RfObject obj, LvlConvertOptions options, int group, bool editorOnly)
    {
        if ((options.Objects & KindOf(obj)) == 0)
        {
            ObjectsFiltered++;
            return;
        }
        Objects.Add(new LvlSceneObject(obj, LvlExtras.TypeName(obj), obj.Uid, obj.Position, OrientationOf(obj), editorOnly, group));
    }

    /// <summary>The orientation an object stores, or null when it has none.</summary>
    public static RfMatrix? OrientationOf(RfObject obj) => obj switch
    {
        RfPlacedObject p => p.Orientation,
        RfKeyframe k => k.Orientation,
        RfRespawnPoint r => r.Orientation,
        RfTrigger t => t.Orientation,
        RfEvent e => e.Orientation,
        RfNavPoint n => n.Orientation,
        RfGeoRegion g => g.Orientation,
        _ => null,
    };

    /// <summary>The object-kind filter bit an object falls under.</summary>
    public static LvlObjectKinds KindOf(RfObject obj) => obj switch
    {
        RfLight => LvlObjectKinds.Lights,
        RfItem => LvlObjectKinds.Items,
        RfClutter => LvlObjectKinds.Clutter,
        RfEntity => LvlObjectKinds.Entities,
        RfTrigger => LvlObjectKinds.Triggers,
        RfEvent => LvlObjectKinds.Events,
        RfRespawnPoint => LvlObjectKinds.Spawns,
        RfNavPoint or RfLegacyWaypoint => LvlObjectKinds.NavPoints,
        RfGeoRegion or RfGasRegion or RfPushRegion or RfClimbingRegion => LvlObjectKinds.Regions,
        RfAmbientSound or RfEaxEffect => LvlObjectKinds.Sounds,
        RfParticleEmitter or RfBoltEmitter or RfDecal or RfRoomEffect => LvlObjectKinds.Effects,
        RfCutsceneCamera or RfCutscenePathNode or RfTarget => LvlObjectKinds.Cameras,
        RfAlpineMesh => LvlObjectKinds.AlpineMeshes,
        RfCorona => LvlObjectKinds.Coronas,
        RfKeyframe => LvlObjectKinds.Keyframes,
        _ => LvlObjectKinds.All,
    };

    // ── Geometry ───────────────────────────────────────────────────────────

    private LvlPart? AddBrush(RfBrush brush, LvlPartKind kind, RfGeometrySource source, bool apply, LvlConvertOptions options, Dictionary<int, int>? groupOf, CancellationToken ct)
    {
        bool applyThis = apply;
        if (!apply && (!IsRotation(brush.Orientation) || !Finite(brush.Position)))
        {
            // A mirrored or skewed orientation has no glTF rotation: its vertices go to world space instead (and a
            // position that is not a number makes every face unreadable, which the report counts).
            applyThis = true;
            _notRotations++;
        }
        var part = new LvlPart
        {
            Kind = kind,
            Name = $"{(kind == LvlPartKind.Mover ? "mover" : "brush")}_{brush.Uid}",
            Uid = brush.Uid,
            Brush = brush,
            Geometry = brush.Geometry,
            TransformApplied = applyThis,
            Position = brush.Position,
            Orientation = brush.Orientation,
            Group = groupOf is not null && groupOf.TryGetValue(brush.Uid, out int g) ? g : -1,
        };
        AddSolid(brush.Geometry, brush, source, applyThis, options, _ => part, ct);
        if (part.Polygons.Count == 0) return null;
        Parts.Add(part);
        return part;
    }

    /// <summary>True when the rows are unit length, at right angles and right-handed in RF's sense (no mirror).</summary>
    public static bool IsRotation(RfMatrix m)
    {
        const float tolerance = 1e-3f;
        if (MathF.Abs(m.Right.LengthSquared() - 1) > tolerance || MathF.Abs(m.Up.LengthSquared() - 1) > tolerance || MathF.Abs(m.Forward.LengthSquared() - 1) > tolerance) return false;
        if (MathF.Abs(Vector3.Dot(m.Right, m.Up)) > tolerance || MathF.Abs(Vector3.Dot(m.Right, m.Forward)) > tolerance || MathF.Abs(Vector3.Dot(m.Up, m.Forward)) > tolerance) return false;
        return Vector3.Dot(Vector3.Cross(m.Right, m.Up), m.Forward) > 0;
    }

    /// <summary>
    /// Adds the kept faces of one solid. Normals are computed over every readable face of the solid (also the ones the
    /// filter leaves out), so filtering never changes the shading of what is kept.
    /// </summary>
    private void AddSolid(RfGeometry g, RfBrush? brush, RfGeometrySource source, bool apply, LvlConvertOptions options, Func<RfFace, LvlPart> partOf, CancellationToken ct)
    {
        var faces = g.Faces;
        var vertices = g.Vertices;
        int n = faces.Length;
        _sourceFaces += n;
        var space = new Vector3[vertices.Length];
        for (int i = 0; i < space.Length; i++) space[i] = apply && brush is not null ? brush.ToWorld(vertices[i]) : vertices[i];

        // Pass 1: which faces are readable, and their area-weighted normals.
        var valid = new bool[n];
        var newell = new Vector3[n];
        var shared = new Dictionary<Vector3, List<int>>();
        for (int i = 0; i < n; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var f = faces[i];
            if (f.Corners.Length < 3)
            {
                _short++;
                continue;
            }
            bool ok = true;
            foreach (var c in f.Corners)
            {
                if ((uint)c.Vertex >= (uint)vertices.Length)
                {
                    _badIndex++;
                    ok = false;
                    break;
                }
                if (!Finite(space[c.Vertex]) || !Finite(c.Uv) || (c.LightmapUv is { } lm && !Finite(lm)))
                {
                    _nonFinite++;
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;
            valid[i] = true;
            newell[i] = Newell(f, space);
            if (f.Smoothing != 0)
            {
                foreach (var c in f.Corners)
                {
                    var key = vertices[c.Vertex];
                    if (!shared.TryGetValue(key, out var list)) shared[key] = list = [];
                    if (list.Count == 0 || list[^1] != i) list.Add(i);
                }
            }
        }

        // Pass 2: the kept faces with per-corner normals.
        var used = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            if (!valid[i]) continue;
            var f = faces[i];
            var classes = RfFaceClasses.Of(f, source, brush);
            if (!options.Keeps(classes))
            {
                foreach (var c in RfFaceClasses.All)
                    if ((classes & c & options.ExcludedFaceClasses) != 0) _filtered[c] = _filtered.GetValueOrDefault(c) + 1;
                if (classes == RfFaceClass.Normal) _filtered[RfFaceClass.Normal] = _filtered.GetValueOrDefault(RfFaceClass.Normal) + 1;
                continue;
            }
            var faceNormal = Unit(newell[i], f, apply ? brush : null);
            var corners = new LvlCorner[f.Corners.Length];
            for (int k = 0; k < corners.Length; k++)
            {
                var c = f.Corners[k];
                var normal = faceNormal;
                if (f.Smoothing != 0 && shared.TryGetValue(vertices[c.Vertex], out var around))
                {
                    var sum = Vector3.Zero;
                    int count = 0;
                    foreach (int j in around)
                    {
                        if ((faces[j].Smoothing & f.Smoothing) == 0) continue;
                        sum += newell[j];
                        count++;
                    }
                    float len = sum.Length();
                    if (count > 1 && len > 1e-20f && float.IsFinite(len))
                    {
                        normal = sum / len;
                        _smoothed++;
                    }
                }
                corners[k] = new LvlCorner(space[c.Vertex], normal, c.Uv, c.LightmapUv);
                used.Add(c.Vertex);
                if (c.Color is not null) _coloredCorners++;
            }
            string texture = g.TextureOf(f) ?? "";
            if (texture.Length > 0 && options.TextureNameMap is { } map) texture = map(texture);
            partOf(f).Polygons.Add(new LvlPolygon(f, f.Texture, texture, classes, faceNormal, corners));
        }
        _sourceVertices += used.Count;
    }

    /// <summary>Newell's normal (length = twice the area), with the orientation of <c>cross(p1 - p0, p2 - p0)</c>.</summary>
    private static Vector3 Newell(RfFace f, Vector3[] space)
    {
        double nx = 0, ny = 0, nz = 0;
        int count = f.Corners.Length;
        for (int k = 0; k < count; k++)
        {
            var a = space[f.Corners[k].Vertex];
            var b = space[f.Corners[(k + 1) % count].Vertex];
            nx += (double)(a.Y - b.Y) * (a.Z + b.Z);
            ny += (double)(a.Z - b.Z) * (a.X + b.X);
            nz += (double)(a.X - b.X) * (a.Y + b.Y);
        }
        return new Vector3((float)nx, (float)ny, (float)nz);
    }

    private Vector3 Unit(Vector3 newell, RfFace f, RfBrush? applied)
    {
        float len = newell.Length();
        if (len > 1e-20f && float.IsFinite(len)) return newell / len;
        // No area: the stored plane says which way the face looks, when there is one.
        _degenerate++;
        if (f.Plane is { } plane && Finite(plane.Normal) && plane.Normal.LengthSquared() > 0.5f)
        {
            var n = applied is not null ? applied.Orientation.Rotate(plane.Normal) : plane.Normal;
            float l = n.Length();
            if (l > 1e-6f) return n / l;
        }
        return Vector3.UnitY;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);

    // ── Report ─────────────────────────────────────────────────────────────

    private void Summarise(LvlReportBuilder report)
    {
        report.SourceFaces = _sourceFaces;
        report.SourceVertices = _sourceVertices;
        if (_filtered.Count > 0)
        {
            string detail = string.Join(", ", _filtered.OrderBy(kv => kv.Key)
                .Select(kv => $"{RfFaceClasses.DisplayName(kv.Key).ToLowerInvariant()} {kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
            report.Info(LvlConvertCodes.FilteredFaces, $"The face filter left out faces ({detail}; a face can be in several classes).", LvlIssueKind.Dropped);
        }
        if (_badIndex > 0)
            report.Warning(LvlConvertCodes.BadVertexFaces, $"{LvlReportBuilder.Count(_badIndex, "face")} name a vertex the solid does not have and were skipped.", LvlIssueKind.Dropped);
        if (_nonFinite > 0)
            report.Warning(LvlConvertCodes.BadVertexFaces, $"{LvlReportBuilder.Count(_nonFinite, "face")} hold a position or texture coordinate that is not a number and were skipped.", LvlIssueKind.Dropped);
        if (_short > 0)
            report.Warning(LvlConvertCodes.ShortFaces, $"{LvlReportBuilder.Count(_short, "face")} with fewer than three corners were skipped.", LvlIssueKind.Dropped);
        if (_degenerate > 0)
            report.Info(LvlConvertCodes.DegenerateFaces, $"{LvlReportBuilder.Count(_degenerate, "face")} have no area; they are kept with the normal of their stored plane.", LvlIssueKind.Approximated);
        if (_smoothed > 0)
            report.Info(LvlConvertCodes.SmoothingNormals, "Normals come from the faces' smoothing groups: faces that share a group are smoothed together, the others keep their own normal.");
        if (_notRotations > 0)
            report.Info(LvlConvertCodes.NotARotation, $"{LvlReportBuilder.Count(_notRotations, "brush", "brushes")} have an orientation that is not a pure rotation (mirrored or skewed); their vertices are written in world space.", LvlIssueKind.Approximated);
        if (_coloredCorners > 0)
            report.Info(LvlConvertCodes.VertexColors, "The level's vertex colours (Red Faction II lighting) are not exported.", LvlIssueKind.Dropped);
        if (Level is { Problems.Length: > 0 } level)
            report.Warning(LvlConvertCodes.SourceProblems, $"The level was read with {LvlReportBuilder.Count(level.Problems.Length, "problem")} (see the level's facts); what could be read is converted.", LvlIssueKind.Dropped);
        if (GroupFile is { Problems.Length: > 0 } file)
            report.Warning(LvlConvertCodes.SourceProblems, $"The group file was read with {LvlReportBuilder.Count(file.Problems.Length, "problem")}; what could be read is converted.", LvlIssueKind.Dropped);
    }
}
