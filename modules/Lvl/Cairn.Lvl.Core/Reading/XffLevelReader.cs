using System.Globalization;
using System.Text.RegularExpressions;
using Cairn.Formats;
using Cairn.Formats.XForge;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Reading;

/// <summary>Options for <see cref="XffLevelReader"/>.</summary>
public sealed record XffLevelReadOptions
{
    public static XffLevelReadOptions Default { get; } = new();

    /// <summary>
    /// Red Faction metres per N-Gage unit (<see cref="XffSpace.RfScale"/>, 2.35, by default; likely, not proven), from
    /// <see cref="XffSpace.MinScale"/> to <see cref="XffSpace.MaxScale"/>; the reader refuses any other.
    /// </summary>
    public float Scale { get; init; } = XffSpace.RfScale;

    /// <summary>
    /// Reads another file the level names (a placed scene such as <c>lvl1a_door01.xff</c>, or a cutscene script) from
    /// beside it (its <c>.cfl</c> or folder); null when it is not there. Without it, placed doors, walls and props have
    /// no geometry and scripts are not checked; both are reported.
    /// </summary>
    public Func<string, byte[]?>? ReadFile { get; init; }

    /// <summary>The object table; null uses <see cref="XffObjectTable.Default"/>.</summary>
    public XffObjectTable? Objects { get; init; }
}

/// <summary>What a level was read from (its scene and the files it placed), so it can be read again at another scale.</summary>
internal sealed class XffLevelSource(XffScene scene, string name, XffLevelReadOptions options)
{
    public XffScene Scene { get; } = scene;
    public string Name { get; } = name;
    public XffLevelReadOptions Options { get; } = options;
    /// <summary>Placed scene files by lower-case name: the scene, or null when missing or unreadable (with the reason).</summary>
    public Dictionary<string, (XffScene? Scene, string? Problem)> Placed { get; } = [];
    /// <summary>Other files asked for (scripts): found or not.</summary>
    public Dictionary<string, bool> Files { get; } = [];
}

/// <summary>
/// Reads a Red Faction for N-Gage level (an <c>.xff</c> scene of kind <see cref="XffKind.Level"/>) into the Red
/// Faction level model, so the preview and every level converter apply unchanged. Everything is moved into Red
/// Faction space once, here (<see cref="XffSpace"/>: positions times the scale, axes kept, triangles (A, B, C) as
/// (A, C, B) with their UVs, normals negated).
/// <list type="bullet">
/// <item>Static geometry: every mesh node in world space with per-corner UVs and its texture name as stored, plus the
/// meshes of the placed scenes that are not objects (doors, buttons, walls, props: their files carry no tracks, the game
/// moves them in code, so they are static). Each area (marker 2013) is a room; <c>portal:&lt;area&gt;</c> boxes are
/// portals. Colliders are never drawn. Alpha-blended materials give alpha faces, additive ones full-bright faces.</item>
/// <item>Brushes, as Red Faction's rooms are made (RED's world starts solid and air brushes cut its rooms): each area's
/// walls, floors and ceilings become one air brush when they close into a room (<see cref="XffAreaShell"/>), the rest of
/// its meshes detail brushes, one per mesh node. An area with no walls of its own (only props) has no air brush; one
/// whose walls do not close is an invisible air box around its meshes (<see cref="AirTexture"/>, <see cref="AirMargin"/>)
/// with all of them detail brushes inside.</item>
/// <item>Objects through <see cref="XffObjectTable"/>: placed enemies, items and the barrel; the player start; 3103
/// multiplayer respawn points; each 42xx spawn point as its entity hidden until the 3116 volume named after it
/// unhides it (a trigger linked to an UnHide event); 3106 script triggers and the level end (2025, 3112) as triggers at
/// their volumes.</item>
/// </list>
/// What has no counterpart is listed in <see cref="XffLevelData"/> and the conversion reports, never guessed.
/// </summary>
public static partial class XffLevelReader
{
    /// <summary>True when <paramref name="name"/> has the <c>.xff</c> extension.</summary>
    public static bool HasExtension(string? name) => XffFile.HasExtension(name);

    /// <summary>Reads a level.</summary>
    /// <exception cref="AssetFormatException">The file is not an X-Forge scene, it is a model or character, not a level, or the scale is out of range.</exception>
    public static RfLevel Read(byte[] data, string name, XffLevelReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(name);
        var scene = XffScene.Read(data, name);
        return FromScene(scene, name, options);
    }

    /// <summary>A level from a scene already read.</summary>
    /// <exception cref="AssetFormatException">The scene is a model or character, not a level, or the scale is out of range.</exception>
    public static RfLevel FromScene(XffScene scene, string name, XffLevelReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var (kind, reason) = XffClassifier.Explain(scene);
        if (kind != XffKind.Level)
            throw new AssetFormatException($"'{name}' is a Red Faction for N-Gage {(kind == XffKind.Character ? "character" : "model")} ({reason}), not a level; the Meshes module opens it.");
        options ??= XffLevelReadOptions.Default;
        return new Builder(new XffLevelSource(scene, name, options), CheckedScale(options.Scale)).Run();
    }

    /// <summary>The same level read again at another scale (the placed files are not read again).</summary>
    /// <exception cref="AssetFormatException">The scale is out of range.</exception>
    internal static RfLevel Rescaled(XffLevelSource source, float scale) => new Builder(source, CheckedScale(scale)).Run();

    /// <summary>The message for a scale out of range (<see cref="XffSpace.IsValidScale"/>).</summary>
    internal static string ScaleProblem => $"The scale must be from {XffSpace.ScaleRangeText} Red Faction metres per N-Gage unit.";

    private static float CheckedScale(float scale) => XffSpace.IsValidScale(scale) ? scale : throw new AssetFormatException(ScaleProblem);

    [GeneratedRegex(@"level_(\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelNumberPattern();

    /// <summary>The marker types of the level end volumes.</summary>
    private const ushort SecondLevelEnd = 3112;

    /// <summary>What a placed scene's own marker says it is (for the report).</summary>
    internal static string PlacedKind(XffScene scene)
    {
        foreach (var m in scene.Markers)
        {
            switch (m.Type)
            {
                case 3107: return "button";
                case 3108: return "door";
                case 3109: return "breakable";
                case 3110: return "pump part";
                case 3113: return "breakable prop";
                case 3114: return "laser barrier";
                case 3115: return "barrel";
            }
        }
        return "prop";
    }

    /// <summary>The texture of the invisible faces of air brushes: the one Red Faction's own levels give most of theirs.</summary>
    internal const string AirTexture = "mtl_invisible02.tga";

    /// <summary>How far an area's air box reaches past its meshes, in Red Faction metres.</summary>
    internal const float AirMargin = 0.5f;

    private sealed class Builder(XffLevelSource source, float scale)
    {
        private readonly XffScene _scene = source.Scene;
        private readonly XffObjectTable _table = source.Options.Objects ?? XffObjectTable.Default;
        private readonly LvlReportBuilder _notes = new();
        private readonly LvlReportBuilder _brushNotes = new();
        private readonly List<string> _problems = [];

        // static geometry
        private readonly List<Vector3> _vertices = [];
        private readonly Dictionary<Vector3, int> _vertexIndex = [];
        private readonly List<string> _textures = [];
        private readonly Dictionary<string, int> _textureIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<RfFace> _faces = [];
        private readonly List<RfBrush> _brushes = [];
        private readonly List<MeshPiece> _pieces = [];
        private RfgBrushBuilder? _brushBuilder;
        private int _meshOrdinal, _alphaFaces, _additiveFaces, _darkeningFaces, _otherBlendFaces, _colouredCorners, _untexturedFaces;
        private readonly SortedSet<string> _otherBlends = new(StringComparer.Ordinal);

        // areas (rooms)
        private readonly List<XffSceneNode> _areas = [];
        private readonly Dictionary<int, int> _areaOfNode = [];
        private readonly Dictionary<int, int> _areaIndex = [];
        private Vector3[] _roomMin = [], _roomMax = [];

        // objects
        private int _nextUid = 1;
        private readonly List<RfEntity> _entities = [];
        private readonly List<RfItem> _items = [];
        private readonly List<RfClutter> _clutter = [];
        private readonly List<RfTrigger> _triggers = [];
        private readonly List<RfEvent> _events = [];
        private readonly List<RfRespawnPoint> _respawns = [];
        private RfPlayerStart? _playerStart;
        private readonly Dictionary<string, List<XffSceneNode>> _unmapped = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _unmappedReason = new(StringComparer.Ordinal);
        private readonly List<XffScriptTrigger> _scripts = [];
        private readonly SortedDictionary<string, string> _placedGeometry = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

        public RfLevel Run()
        {
            _brushBuilder = new RfgBrushBuilder(RfgBrushKind.Detail, _brushNotes);
            FindAreas();

            // the level's own meshes, then the placed scenes that are geometry
            foreach (var node in _scene.Nodes)
                if (node.Meshes.Count > 0) AddMeshNode(node, node.WorldMatrix, AreaOf(node), node.Name, placed: false);
            var objects = new List<(XffSceneNode Node, XffObjectClass Row)>();
            foreach (var prefab in _scene.Prefabs) PlaceOrQueue(prefab, objects);

            var geometry = BuildGeometry(out int portals, out int roomBoxes);
            AddBrushes(geometry);
            _brushBuilder.Summarise();

            _nextUid = _brushes.Count + 1;
            foreach (var (node, row) in objects) AddObject(node, row);
            AddMarkers();
            Summarise(geometry);

            string? number = LevelNumberPattern().Match(source.Name) is { Success: true } m ? m.Groups[1].Value : null;
            var data = new XffLevelData
            {
                Scale = scale,
                LevelNumber = number,
                Areas = [.. _areas.Select(a => a.Name)],
                RoomBoxes = roomBoxes,
                Portals = portals,
                Triangles = geometry.Faces.Length,
                Textures = [.. _textures],
                PlacedGeometry = [.. _placedGeometry.Select(kv => $"{kv.Key} ({kv.Value})")],
                MissingFiles = [.. _missing],
                Unmapped = [.. _unmapped.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new XffUnmapped(kv.Key, kv.Value.Count, _unmappedReason[kv.Key], [.. kv.Value.Select(n => n.Name).Distinct(StringComparer.Ordinal).Take(6)]))],
                Scripts = [.. _scripts],
                Notes = _notes.Build().Issues,
                BrushNotes = _brushNotes.Build().Issues,
                Source = source,
            };
            var problems = _scene.Problems.Concat(_problems).ToList();
            return new RfLevel
            {
                Name = source.Name,
                Header = new RfLevelHeader { LevelName = Path.GetFileNameWithoutExtension(source.Name) },
                StaticGeometry = geometry,
                Brushes = [.. _brushes],
                Objects = new RfObjectSet
                {
                    Entities = [.. _entities],
                    Items = [.. _items],
                    Clutter = [.. _clutter],
                    Triggers = [.. _triggers],
                    Events = [.. _events],
                    RespawnPoints = [.. _respawns],
                },
                PlayerStart = _playerStart,
                Problems = [.. problems],
                Xff = data,
            };
        }

        // ── Space ──────────────────────────────────────────────────────────

        private Vector3 Position(Vector3 xff, Matrix4x4 world) => Vector3.Transform(xff, world) * scale;

        private static Vector3 Normal(Vector3 xff, Matrix4x4 world)
        {
            var n = XffSpace.ToRfNormal(Vector3.TransformNormal(xff, world));
            float length = n.Length();
            return length > 1e-12f && float.IsFinite(length) ? n / length : Vector3.Zero;
        }

        /// <summary>The rotation of a world matrix as Red Faction rows (the axes are the same; scale is taken out).</summary>
        private static RfMatrix Orientation(Matrix4x4 world)
        {
            static Vector3 Unit(Vector3 v, Vector3 fallback)
            {
                float l = v.Length();
                return l > 1e-6f && float.IsFinite(l) ? v / l : fallback;
            }
            return new RfMatrix(Unit(new Vector3(world.M11, world.M12, world.M13), Vector3.UnitX),
                Unit(new Vector3(world.M21, world.M22, world.M23), Vector3.UnitY),
                Unit(new Vector3(world.M31, world.M32, world.M33), Vector3.UnitZ));
        }

        private Vector3 Place(Matrix4x4 world) => Position(Vector3.Zero, world);

        // ── Areas ──────────────────────────────────────────────────────────

        private void FindAreas()
        {
            foreach (var node in _scene.Nodes)
            {
                if (node.Type != XffMarkers.Area) continue;
                _areaIndex[node.Index] = _areas.Count;
                _areas.Add(node);
            }
            _roomMin = [.. _areas.Select(_ => new Vector3(float.MaxValue))];
            _roomMax = [.. _areas.Select(_ => new Vector3(float.MinValue))];
        }

        /// <summary>
        /// The room (area index) a node lies in: its nearest area ancestor (itself included), or -1. Every node on the way
        /// up is remembered, so each node is walked once however deep the tree is.
        /// </summary>
        private int AreaOf(XffSceneNode node)
        {
            var path = new List<XffSceneNode>();
            int area = -1;
            for (var n = node; n is not null; n = n.Parent)
            {
                if (_areaOfNode.TryGetValue(n.Index, out int known)) { area = known; break; }
                path.Add(n);
                if (n.Type == XffMarkers.Area) { area = _areaIndex[n.Index]; break; }
            }
            foreach (var n in path) _areaOfNode[n.Index] = area;
            return area;
        }

        private void Grow(int room, Vector3 p)
        {
            if (room < 0) return;
            _roomMin[room] = Vector3.Min(_roomMin[room], p);
            _roomMax[room] = Vector3.Max(_roomMax[room], p);
        }

        // ── Geometry ───────────────────────────────────────────────────────

        private int Vertex(Vector3 p)
        {
            if (_vertexIndex.TryGetValue(p, out int i)) return i;
            i = _vertices.Count;
            _vertices.Add(p);
            _vertexIndex[p] = i;
            return i;
        }

        private int Texture(string name)
        {
            if (_textureIndex.TryGetValue(name, out int i)) return i;
            i = _textures.Count;
            _textures.Add(name);
            _textureIndex[name] = i;
            return i;
        }

        /// <summary>The face flags a material's blend gives (A5: (5, 6) alpha, (2, 2) additive, (1, 4) darkening, (2, 1) opaque).</summary>
        private RfFaceFlags BlendFlags(XffMaterial? material, int triangles)
        {
            if (material is null) return RfFaceFlags.None;
            switch ((material.UnknownByte0, material.UnknownByte1))
            {
                case (2, 1):
                    return RfFaceFlags.None;
                case (5, 6):
                    _alphaFaces += triangles;
                    return RfFaceFlags.HasAlpha;
                case (2, 2):
                    _additiveFaces += triangles;
                    return RfFaceFlags.FullBright;
                case (1, 4):
                    _darkeningFaces += triangles;
                    return RfFaceFlags.HasAlpha;
                default:
                    _otherBlendFaces += triangles;
                    _otherBlends.Add($"({material.UnknownByte0}, {material.UnknownByte1})");
                    return RfFaceFlags.None;
            }
        }

        /// <summary>A mesh node's triangles on their way to brushes; <see cref="Placed"/> when a placed scene's (a door, a wall).</summary>
        private sealed record MeshPiece(string Label, int Room, bool Placed, List<XffMeshTriangle> Triangles);

        /// <summary>Adds one mesh node's triangles to the static geometry, and keeps them for the brushes.</summary>
        private void AddMeshNode(XffSceneNode node, Matrix4x4 world, int room, string label, bool placed)
        {
            uint smoothingBit = 1u << (_meshOrdinal++ % 32);
            var corners = new List<(Vector3 P, Vector2 Uv, Vector3 N)[]>();
            var faceData = new List<(string Texture, RfFaceFlags Flags, uint Smoothing)>();
            foreach (var mesh in node.Meshes)
            {
                foreach (var sub in mesh.Submeshes)
                {
                    var flags = BlendFlags(sub.Material, sub.TriangleCount);
                    bool uvs = sub.CornerUvs.Count == sub.Indices.Count;
                    bool normals = sub.Normals.Count == sub.Positions.Count;
                    if (sub.CornerColours.Count > 0) _colouredCorners += sub.CornerColours.Count;
                    string texture = uvs && sub.TextureName is { Length: > 0 } t ? t : "";
                    if (texture.Length == 0) _untexturedFaces += sub.TriangleCount;
                    for (int tri = 0; tri < sub.TriangleCount; tri++)
                    {
                        var (a, b, c) = XffSpace.ToRfTriangle(tri * 3, tri * 3 + 1, tri * 3 + 2);
                        var face = new (Vector3 P, Vector2 Uv, Vector3 N)[3];
                        int k = 0;
                        foreach (int corner in new[] { a, b, c })
                        {
                            int v = sub.Indices[corner];
                            face[k++] = (Position(sub.Positions[v], world), uvs ? sub.CornerUvs[corner] : Vector2.Zero,
                                normals ? Normal(sub.Normals[v], world) : Vector3.Zero);
                        }
                        corners.Add(face);
                        faceData.Add((texture, flags, Bent(face) ? smoothingBit : 0u));
                    }
                }
            }
            if (corners.Count == 0) return;

            // the static geometry
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (int i = 0; i < corners.Count; i++)
            {
                var face = corners[i];
                var (texture, flags, smoothing) = faceData[i];
                var cross = Vector3.Cross(face[1].P - face[0].P, face[2].P - face[0].P);
                float length = cross.Length();
                RfPlane? plane = length > 1e-12f && float.IsFinite(length) ? new RfPlane(cross / length, -Vector3.Dot(cross / length, face[0].P)) : null;
                _faces.Add(new RfFace
                {
                    Plane = plane,
                    Texture = texture.Length > 0 ? Texture(texture) : -1,
                    Flags = flags,
                    RawFlags = (uint)flags,
                    Smoothing = smoothing,
                    Room = room,
                    Corners = [.. face.Select(c => new RfCorner(Vertex(c.P), c.Uv))],
                });
                foreach (var c in face)
                {
                    min = Vector3.Min(min, c.P);
                    max = Vector3.Max(max, c.P);
                    Grow(room, c.P);
                }
            }

            _pieces.Add(new MeshPiece(label, room, placed, [.. corners.Select((face, i) => new XffMeshTriangle(face, faceData[i].Texture, faceData[i].Flags, faceData[i].Smoothing))]));
        }

        /// <summary>How many boxed areas the report names.</summary>
        private const int ListedAreas = 12;

        /// <summary>
        /// The brushes, as Red Faction's rooms are made (RED's world starts solid and its build cuts rooms out of it with air
        /// brushes): each area's opaque walls, floors and ceilings (not the placed doors and walls) as one air brush when they
        /// close into a room (<see cref="XffAreaShell"/>); every other face of a mesh node (the meshes in no area too: signs and
        /// posters inside the rooms) as that node's detail brush; nothing for an area with no walls of its own; and an
        /// invisible air box around an area whose walls do not close.
        /// </summary>
        private void AddBrushes(RfGeometry geometry)
        {
            // where each area's corners are, to tell a hole that leads into another area
            var cells = new Dictionary<(int, int, int), List<(Vector3 P, int Room)>>();
            static (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / XffAreaShell.Weld), (int)MathF.Floor(p.Y / XffAreaShell.Weld), (int)MathF.Floor(p.Z / XffAreaShell.Weld));
            foreach (var f in geometry.Faces)
            {
                if (f.Room < 0) continue;
                foreach (var c in f.Corners)
                {
                    var p = geometry.Vertices[c.Vertex];
                    if (!cells.TryGetValue(Cell(p), out var list)) cells[Cell(p)] = list = [];
                    list.Add((p, f.Room));
                }
            }
            bool InOtherArea(Vector3 p, int room)
            {
                var (x, y, z) = Cell(p);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                            if (cells.TryGetValue((x + dx, y + dy, z + dz), out var list) && list.Any(q => q.Room != room && Vector3.DistanceSquared(q.P, p) <= XffAreaShell.Weld * XffAreaShell.Weld))
                                return true;
                return false;
            }

            var used = _pieces.Select(_ => new HashSet<int>()).ToList();
            var boxed = new List<(int Area, string Reason)>();
            var wallless = new List<int>();
            int doorways = 0, sealedHoles = 0, rooms = 0;
            for (int area = 0; area < _areas.Count; area++)
            {
                if (!(_roomMin[area].X <= _roomMax[area].X)) continue;
                var triangles = new List<XffMeshTriangle>();
                var from = new List<(int Piece, int Triangle)>();
                for (int i = 0; i < _pieces.Count; i++)
                {
                    var piece = _pieces[i];
                    if (piece.Room != area || piece.Placed) continue;
                    for (int t = 0; t < piece.Triangles.Count; t++)
                    {
                        if (piece.Triangles[t].Flags != RfFaceFlags.None) continue;
                        triangles.Add(piece.Triangles[t]);
                        from.Add((i, t));
                    }
                }
                var portals = geometry.Portals.Where(p => p.RoomA == area || p.RoomB == area)
                    .Select(p => (Min: Vector3.Min(p.Point1, p.Point2) - new Vector3(0.1f), Max: Vector3.Max(p.Point1, p.Point2) + new Vector3(0.1f))).ToList();
                bool IsDoorway(IReadOnlyList<Vector3> hole) =>
                    portals.Any(b => hole.All(p => Vector3.Min(p, b.Min) == b.Min && Vector3.Max(p, b.Max) == b.Max)) || hole.All(p => InOtherArea(p, area));
                var shell = triangles.Count > 0 ? XffAreaShell.Build($"room {_areas[area].Name}", triangles, IsDoorway) : null;
                string? problem = shell is null ? XffAreaShell.NoWalls : shell.Problem;
                if (problem is null && _brushBuilder!.Build(shell!.Brush!, _brushes.Count + 1) is { } built)
                {
                    if (XffAreaShell.Sealed(built.Geometry) is (true, < 0))
                    {
                        _brushes.Add(built);
                        foreach (int t in shell.Used) used[from[t].Piece].Add(from[t].Triangle);
                        doorways += shell.Doorways;
                        sealedHoles += shell.Sealed;
                        rooms++;
                        continue;
                    }
                    problem = "faces too small for RED";
                }
                if (problem == XffAreaShell.NoWalls) wallless.Add(area);
                else boxed.Add((area, problem ?? "faces too small for RED"));
            }

            // what is left of each mesh node: its detail brush, at the middle of its bounds
            var details = new List<(string Label, RfBrush Brush)>();
            for (int i = 0; i < _pieces.Count; i++)
            {
                var piece = _pieces[i];
                var left = piece.Triangles.Where((_, t) => !used[i].Contains(t)).ToList();
                if (left.Count == 0) continue;
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var c in left.SelectMany(t => t.Corners))
                {
                    min = Vector3.Min(min, c.P);
                    max = Vector3.Max(max, c.P);
                }
                var centre = (min + max) / 2;
                var brush = new RfgSourceBrush { Name = piece.Label, Position = centre };
                foreach (var t in left)
                    brush.Faces.Add(new RfgSourceFace([.. t.Corners.Select(c => new RfgSourceCorner(c.P - centre, c.Uv, c.N == Vector3.Zero ? null : c.N))], t.Texture) { Flags = t.Flags, Smoothing = t.Smoothing });
                if (_brushBuilder!.Build(brush, _brushes.Count + 1) is { } built)
                {
                    _brushes.Add(built);
                    details.Add((piece.Label, built));
                }
            }

            int boxes = 0;
            foreach (var (area, _) in boxed)
                if (AddAirBrush($"air {_areas[area].Name}", _roomMin[area], _roomMax[area])) boxes++;

            static string N(int n, string one, string? many = null) => LvlReportBuilder.Count(n, one, many);
            string weld = XffAreaShell.Weld.ToString("0.###", CultureInfo.InvariantCulture), depth = XffAreaShell.HoleDepth.ToString("0.##", CultureInfo.InvariantCulture);
            if (rooms > 0)
                _brushNotes.Info(LvlConvertCodes.XffLevel, $"{N(rooms, "area became an air brush", "areas became air brushes")} made of {(rooms == 1 ? "its" : "their")} own walls, floors and ceilings, as Red Faction's rooms are (RED's world starts solid and its build cuts rooms out of it with air brushes). "
                    + $"Their corners were joined within {weld} m and where a corner lay on another face's edge; {N(doorways, "doorway")} (the portal volumes, or holes that meet another area) became short tunnels {depth} m into the next area, so neighbouring rooms overlap (their sides carry on the walls beside them, their far ends are invisible)"
                    + (sealedHoles > 0 ? $"; {N(sealedHoles, "other hole", "other holes")} (where glass, a grate or a placed door sits, or the N-Gage level shows nothing) became {depth} m recesses textured like the wall beside them." : "."), LvlIssueKind.Approximated);
            if (wallless.Count > 0)
                _brushNotes.Info(LvlConvertCodes.XffLevel, $"{N(wallless.Count, "area has", "areas have")} no walls of {(wallless.Count == 1 ? "its" : "their")} own (only props, or see-through walls), so no air brush; {(wallless.Count == 1 ? "its" : "their")} meshes are detail brushes in the rooms around {(wallless.Count == 1 ? "it" : "them")}: "
                    + string.Join(", ", wallless.Take(ListedAreas).Select(a => _areas[a].Name)) + (wallless.Count > ListedAreas ? $" and {wallless.Count - ListedAreas} more" : "") + ".", LvlIssueKind.Approximated);
            if (boxes > 0)
                _brushNotes.Warning(LvlConvertCodes.XffLevel, $"{N(boxes, "area's walls do", "areas' walls do")} not close into a room, so {(boxes == 1 ? "it is" : "each is")} an invisible air box ({AirTexture}) around its meshes, {AirMargin.ToString("0.##", CultureInfo.InvariantCulture)} m larger on every side, with its meshes as detail brushes inside: "
                    + string.Join(", ", boxed.Take(ListedAreas).Select(b => $"{_areas[b.Area].Name} ({b.Reason})")) + (boxed.Count > ListedAreas ? $" and {boxed.Count - ListedAreas} more" : "") + ". Where a box overlaps a room, RED's build removes that room's walls there (air in air): reshape the boxes in RED.", LvlIssueKind.Approximated);
            if (details.Count > 0)
                _brushNotes.Info(LvlRfgCodes.OpenMesh, $"The other faces of the meshes (props, decals, see-through and glowing parts, the placed doors and walls, the meshes in no area, and the areas without a room of their own) are detail brushes, one per mesh: {N(details.Count, "brush", "brushes")}. RED draws them and collides with them as they are.", LvlIssueKind.Approximated);

            // a detail brush wholly beyond every air brush's bounds is in RED's solid, in no room, so it is never seen (one partly
            // in a wall or ceiling, a stalactite or a sign, is as RED's own levels have them)
            var air = _brushes.Where(b => b.IsAir).Select(b => (Min: b.Position + b.Geometry.Vertices.Aggregate(Vector3.Min) - new Vector3(0.01f), Max: b.Position + b.Geometry.Vertices.Aggregate(Vector3.Max) + new Vector3(0.01f))).ToList();
            var buried = details.Where(d => d.Brush.Geometry.Vertices.All(v => !air.Any(a => Vector3.Min(d.Brush.Position + v, a.Min) == a.Min && Vector3.Max(d.Brush.Position + v, a.Max) == a.Max))).Select(d => d.Label).ToList();
            if (buried.Count > 0)
                _brushNotes.Warning(LvlConvertCodes.XffLevel, $"{N(buried.Count, "detail brush lies", "detail brushes lie")} wholly beyond every air brush, in RED's solid, where {(buried.Count == 1 ? "it is" : "they are")} in no room and never seen: {string.Join(", ", buried.Take(ListedAreas))}{(buried.Count > ListedAreas ? $" and {buried.Count - ListedAreas} more" : "")}. Move {(buried.Count == 1 ? "it" : "them")} or widen an air brush in RED.", LvlIssueKind.Approximated);
        }

        /// <summary>Adds an air brush that fills the box from <paramref name="min"/> to <paramref name="max"/>, grown by <see cref="AirMargin"/>; false when nothing was built.</summary>
        private bool AddAirBrush(string label, Vector3 min, Vector3 max)
        {
            var centre = (min + max) / 2;
            var grown = (max - min) / 2 + new Vector3(AirMargin);
            float[] half = [grown.X, grown.Y, grown.Z];
            var brush = new RfgSourceBrush { Name = label, Position = centre, SolidFlags = RfSolidFlags.Air };
            for (int axis = 0; axis < 3; axis++)
            {
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                foreach (float side in (ReadOnlySpan<float>)[-1f, 1f])
                {
                    // (u, v) corners in this order face +axis; the face points into the box, so the +axis side turns them over (UVs: one repeat per 4 m)
                    (float A, float B)[] square = [(-1, -1), (1, -1), (1, 1), (-1, 1)];
                    if (side > 0) Array.Reverse(square);
                    var corners = new RfgSourceCorner[4];
                    for (int k = 0; k < 4; k++)
                    {
                        var p = new float[3];
                        p[axis] = side * half[axis];
                        p[u] = square[k].A * half[u];
                        p[v] = square[k].B * half[v];
                        corners[k] = new RfgSourceCorner(new Vector3(p[0], p[1], p[2]), new Vector2(p[u], p[v]) / 4f);
                    }
                    brush.Faces.Add(new RfgSourceFace(corners, AirTexture) { Flags = RfFaceFlags.Invisible });
                }
            }
            if (_brushBuilder!.Build(brush, _brushes.Count + 1) is not { } built) return false;
            _brushes.Add(built);
            return true;
        }

        /// <summary>True when the stored normals bend away from the face (a smooth surface), so the face joins its mesh's smoothing group.</summary>
        private static bool Bent((Vector3 P, Vector2 Uv, Vector3 N)[] face)
        {
            var cross = Vector3.Cross(face[1].P - face[0].P, face[2].P - face[0].P);
            float length = cross.Length();
            if (!(length > 1e-12f) || !float.IsFinite(length)) return false;
            var normal = cross / length;
            foreach (var c in face)
                if (c.N != Vector3.Zero && Vector3.Dot(c.N, normal) < 0.9995f) return true;
            return false;
        }

        /// <summary>A placed scene: an object of the table (queued), left out, or geometry from its own file.</summary>
        private void PlaceOrQueue(XffSceneNode node, List<(XffSceneNode Node, XffObjectClass Row)> objects)
        {
            string file = node.Prefab!.FileName;
            if (_table.Find(file) is { } row)
            {
                objects.Add((node, row));
                return;
            }
            if (_table.LeftOutReason(file) is { } reason)
            {
                Unmapped(Path.GetFileName(file).ToLowerInvariant(), reason, node);
                return;
            }
            var (scene, problem) = PlacedScene(file);
            if (scene is null)
            {
                if (problem is null) _missing.Add(file);
                else _problems.Add(problem);
                return;
            }
            var kind = XffClassifier.Classify(scene);
            if (kind != XffKind.Model)
            {
                Unmapped(Path.GetFileName(file).ToLowerInvariant(), $"a {(kind == XffKind.Character ? "character" : "level")} the object table does not list", node);
                return;
            }
            int room = AreaOf(node);
            foreach (var part in scene.Nodes)
                if (part.Meshes.Count > 0) AddMeshNode(part, part.WorldMatrix * node.WorldMatrix, room, $"{node.Name}/{part.Name}", placed: true);
            _placedGeometry.TryAdd(Path.GetFileName(file).ToLowerInvariant(), PlacedKind(scene));
        }

        private (XffScene? Scene, string? Problem) PlacedScene(string file)
        {
            string key = Path.GetFileName(file.Replace('\\', '/')).ToLowerInvariant();
            if (source.Placed.TryGetValue(key, out var known)) return known;
            (XffScene?, string?) result = (null, null);
            try
            {
                if (source.Options.ReadFile?.Invoke(Path.GetFileName(file)) is { } bytes) result = (XffScene.Read(bytes, Path.GetFileName(file)), null);
            }
            catch (AssetFormatException e)
            {
                result = (null, $"The placed scene {file} could not be read ({e.Message}); its geometry is left out.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                result = (null, $"The placed scene {file} could not be read ({e.Message}); its geometry is left out.");
            }
            return source.Placed[key] = result;
        }

        private bool? FileExists(string name)
        {
            if (source.Options.ReadFile is not { } read) return null;
            if (source.Files.TryGetValue(name, out bool known)) return known;
            bool found;
            try { found = read(name) is not null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or AssetFormatException) { found = false; }
            return source.Files[name] = found;
        }

        private RfGeometry BuildGeometry(out int portals, out int roomBoxes)
        {
            // rooms: the areas, grown by their walkable room boxes; portals: the portal boxes between areas
            var portalList = new List<RfPortal>();
            int unresolved = 0;
            roomBoxes = 0;
            var areaByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _areas.Count; i++) areaByName.TryAdd(_areas[i].Name, i);
            foreach (var node in _scene.Nodes)
            {
                if (node.Chunk is not XffCollision) continue;
                foreach (var property in node.Properties)
                {
                    if (property.Text == XffClassifier.RoomProperty)
                    {
                        roomBoxes++;
                        if (Volume(node) is { } box) { Grow(AreaOf(node), box.Min); Grow(AreaOf(node), box.Max); }
                    }
                    else if (property.Key == "portal" && property.Value is { } target)
                    {
                        int a = AreaOf(node);
                        if (a >= 0 && areaByName.TryGetValue(target, out int b) && b != a && Volume(node) is { } box)
                            portalList.Add(new RfPortal(a, b, box.Min, box.Max));
                        else unresolved++;
                    }
                }
            }
            portals = portalList.Count;
            if (unresolved > 0)
                _notes.Info(LvlConvertCodes.XffLevel, $"{LvlReportBuilder.Count(unresolved, "portal box", "portal boxes")} join an area to itself or to no area and are not portals.", LvlIssueKind.Dropped);
            var rooms = new List<RfRoom>();
            for (int i = 0; i < _areas.Count; i++)
            {
                bool any = _roomMin[i].X <= _roomMax[i].X;
                rooms.Add(new RfRoom { Id = i, BoundsMin = any ? _roomMin[i] : Place(_areas[i].WorldMatrix), BoundsMax = any ? _roomMax[i] : Place(_areas[i].WorldMatrix) });
            }
            return new RfGeometry
            {
                Name = Path.GetFileNameWithoutExtension(source.Name),
                Textures = [.. _textures],
                Rooms = [.. rooms],
                Portals = [.. portalList],
                Vertices = [.. _vertices],
                Faces = [.. _faces],
            };
        }

        // ── Volumes ────────────────────────────────────────────────────────

        /// <summary>A volume in Red Faction space: an oriented box (centre, rows, full size) and its world bounds.</summary>
        private readonly record struct Box(Vector3 Centre, RfMatrix Orientation, Vector3 Size, Vector3 Min, Vector3 Max);

        /// <summary>The volume of a collider node: its box, or the bounds of the mesh it is fitted to; null for neither.</summary>
        private Box? Volume(XffSceneNode node)
        {
            if (node.Chunk is not XffCollision collision) return null;
            if (collision.IsBox)
            {
                var w = node.WorldMatrix;
                var lo = Vector3.Min(collision.Min, collision.Max);
                var hi = Vector3.Max(collision.Min, collision.Max);
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                for (int i = 0; i < 8; i++)
                {
                    var p = Position(new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z), w);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                var axes = new Vector3(new Vector3(w.M11, w.M12, w.M13).Length(), new Vector3(w.M21, w.M22, w.M23).Length(), new Vector3(w.M31, w.M32, w.M33).Length());
                var size = (hi - lo) * axes * scale;
                if (!RfgBrushBuilder.Finite(size) || !RfgBrushBuilder.Finite(min) || !RfgBrushBuilder.Finite(max)) return null;
                return new Box(Position((lo + hi) / 2, w), Orientation(w), size, min, max);
            }
            if (collision.UnknownForm == XffCollision.NodeForm && _scene.NodeById(collision.Source) is { } fitted)
            {
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var mesh in fitted.Meshes)
                    foreach (var sub in mesh.Submeshes)
                        foreach (int v in sub.Indices)
                        {
                            var p = Position(sub.Positions[v], fitted.WorldMatrix);
                            min = Vector3.Min(min, p);
                            max = Vector3.Max(max, p);
                        }
                if (!(min.X <= max.X) || !RfgBrushBuilder.Finite(min) || !RfgBrushBuilder.Finite(max)) return null;
                return new Box((min + max) / 2, RfMatrix.Identity, max - min, min, max);
            }
            return null;
        }

        /// <summary>The volume of a marker: its collider child's box; null when it has none.</summary>
        private Box? VolumeOf(XffSceneNode marker)
        {
            foreach (var child in marker.Children)
                if (child.Chunk is XffCollision && Volume(child) is { } box) return box;
            return null;
        }

        // ── Objects ────────────────────────────────────────────────────────

        private int Uid() => _nextUid++;

        private void Unmapped(string what, string reason, XffSceneNode node)
        {
            if (!_unmapped.TryGetValue(what, out var list)) _unmapped[what] = list = [];
            list.Add(node);
            _unmappedReason.TryAdd(what, reason);
        }

        private void AddObject(XffSceneNode node, XffObjectClass row)
        {
            var w = node.WorldMatrix;
            var position = Place(w);
            var orientation = Orientation(w);
            switch (row.Kind)
            {
                case XffObjectKind.Entity:
                    _entities.Add(new RfEntity { Uid = Uid(), ClassName = row.ClassName, Position = position, Orientation = orientation, ScriptName = node.Name });
                    break;
                case XffObjectKind.Item:
                    _items.Add(new RfItem { Uid = Uid(), ClassName = row.ClassName, Position = position, Orientation = orientation, ScriptName = node.Name, Count = row.Count ?? 1, RespawnTime = -1 });
                    break;
                case XffObjectKind.Clutter:
                    _clutter.Add(new RfClutter { Uid = Uid(), ClassName = row.ClassName, Position = position, Orientation = orientation, ScriptName = node.Name });
                    break;
                case XffObjectKind.PlayerStart:
                    if (_playerStart is null) _playerStart = new RfPlayerStart(position, orientation);
                    else Unmapped(row.FileName, "a second player start (the first is kept)", node);
                    break;
            }
        }

        private void AddMarkers()
        {
            // what the level places, by file, so a spawn point's enemy type finds the entity the level itself uses
            var placed = new HashSet<string>(_scene.Prefabs.Select(p => Path.GetFileName(p.Prefab!.FileName)), StringComparer.OrdinalIgnoreCase);
            var spawnsByName = new Dictionary<string, List<RfEntity>>(StringComparer.OrdinalIgnoreCase);
            var spawnEntities = new List<int>();
            var volumes = new List<XffSceneNode>();
            int noVolume = 0;
            foreach (var marker in _scene.Markers)
            {
                ushort type = marker.Type;
                var w = marker.WorldMatrix;
                switch (type)
                {
                    case XffMarkers.Area:
                        break;
                    case XffMarkers.DeathmatchSpawn:
                        _respawns.Add(new RfRespawnPoint { Uid = Uid(), Position = Place(w), Orientation = Orientation(w), ScriptName = marker.Name });
                        break;
                    case >= 4200 and <= 4299:
                    {
                        int enemy = XffMarkers.SpawnedEnemyType(type)!.Value;
                        var rows = _table.ForMarker(enemy);
                        var row = rows.FirstOrDefault(r => placed.Contains(r.FileName)) ?? rows.FirstOrDefault();
                        if (row is null)
                        {
                            Unmapped($"spawn point {type}", $"its enemy type {enemy} has no class in the object table", marker);
                            break;
                        }
                        var entity = new RfEntity { Uid = Uid(), ClassName = row.ClassName, Position = Place(w), Orientation = Orientation(w), ScriptName = marker.Name, StartHidden = true };
                        _entities.Add(entity);
                        if (!spawnsByName.TryGetValue(marker.Name, out var list)) spawnsByName[marker.Name] = list = [];
                        list.Add(entity);
                        break;
                    }
                    case XffMarkers.EnemyGroup or XffMarkers.ScriptTrigger or XffMarkers.LevelEnd or SecondLevelEnd:
                        volumes.Add(marker);
                        break;
                    default:
                        Unmapped($"marker {type}", MarkerReason(type), marker);
                        break;
                }
            }

            // the volumes, now that every spawn point is known
            var released = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var emptyVolumes = new List<string>();
            int levelEnds = 0;
            foreach (var marker in volumes)
            {
                if (VolumeOf(marker) is not { } box)
                {
                    noVolume++;
                    Unmapped($"marker {marker.Type}", "it has no collider to give its volume", marker);
                    continue;
                }
                switch (marker.Type)
                {
                    case XffMarkers.EnemyGroup:
                    {
                        string spawn = marker.Name.StartsWith("gr_", StringComparison.OrdinalIgnoreCase) ? marker.Name[3..] : marker.Name;
                        if (!spawnsByName.TryGetValue(spawn, out var entities))
                        {
                            emptyVolumes.Add(marker.Name);
                            continue;
                        }
                        released.Add(spawn);
                        int trigger = Uid(), unhide = Uid();
                        _triggers.Add(Trigger(trigger, marker.Name, box, [unhide]));
                        _events.Add(new RfEvent { Uid = unhide, ClassName = "UnHide", ScriptName = "unhide " + spawn, Position = box.Centre, Links = [.. entities.Select(e => e.Uid)] });
                        break;
                    }
                    case XffMarkers.ScriptTrigger:
                    {
                        string script = marker.Name.StartsWith("gr_", StringComparison.OrdinalIgnoreCase) ? marker.Name[3..] : marker.Name;
                        string? number = LevelNumberPattern().Match(source.Name) is { Success: true } m ? m.Groups[1].Value : null;
                        string file = number is null ? "" : $"C{number}_{script}.txt";
                        _scripts.Add(new XffScriptTrigger(marker.Name, file, file.Length > 0 ? FileExists(file) : null));
                        _triggers.Add(Trigger(Uid(), marker.Name, box, []));
                        break;
                    }
                    default:
                        levelEnds++;
                        _triggers.Add(Trigger(Uid(), marker.Name, box, []));
                        break;
                }
            }

            var neverShown = spawnsByName.Where(kv => !released.Contains(kv.Key)).ToList();
            int spawned = spawnsByName.Values.Sum(l => l.Count);
            if (spawned > 0)
                _notes.Info(LvlConvertCodes.XffObjects, $"{LvlReportBuilder.Count(spawned, "enemy spawn point")} (42xx) became entities hidden at the start; each enemy-group volume (3116) became a trigger linked to an UnHide event that shows the spawn points named after it, as the N-Gage level releases them when the player walks in. Enemies placed in the level (its enemy_*.xff files) are there from the start; spawn points and placed enemies are never merged.", LvlIssueKind.Approximated);
            if (neverShown.Count > 0)
                _notes.Warning(LvlConvertCodes.XffObjects, $"{LvlReportBuilder.Count(neverShown.Sum(kv => kv.Value.Count), "spawn point")} ({string.Join(", ", neverShown.Select(kv => kv.Key))}) {(neverShown.Count == 1 ? "has" : "have")} no volume named after {(neverShown.Count == 1 ? "it" : "them")}; the entities stay hidden.", LvlIssueKind.Approximated);
            if (emptyVolumes.Count > 0)
                _notes.Info(LvlConvertCodes.XffObjects, $"{LvlReportBuilder.Count(emptyVolumes.Count, "enemy-group volume")} name no spawn point ({string.Join(", ", emptyVolumes)}) and {(emptyVolumes.Count == 1 ? "was" : "were")} left out.", LvlIssueKind.Dropped);
            if (_scripts.Count > 0)
            {
                var missing = _scripts.Where(s => s.Found == false).Select(s => s.ScriptFile).ToList();
                _notes.Info(LvlConvertCodes.XffObjects, $"{LvlReportBuilder.Count(_scripts.Count, "script trigger")} (3106) became triggers at their volumes; Red Faction cannot run the N-Gage cutscene scripts, so they do nothing until linked to events: {string.Join(", ", _scripts.Select(s => s.ScriptFile.Length > 0 ? $"{s.TriggerName} runs {s.ScriptFile}" : s.TriggerName))}."
                    + (missing.Count > 0 ? $" Not beside the level: {string.Join(", ", missing)}." : ""), LvlIssueKind.Approximated);
            }
            if (levelEnds > 0)
                _notes.Info(LvlConvertCodes.XffObjects, $"{LvlReportBuilder.Count(levelEnds, "level end volume")} became {(levelEnds == 1 ? "a trigger" : "triggers")}; link {(levelEnds == 1 ? "it" : "them")} to a Load_Level event naming the next level.", LvlIssueKind.Approximated);
            if (noVolume > 0)
                _problems.Add($"{LvlReportBuilder.Count(noVolume, "trigger marker has", "trigger markers have")} no collider giving the volume and {(noVolume == 1 ? "was" : "were")} left out.");
        }

        private static RfTrigger Trigger(int uid, string name, Box box, ImmutableArray<int> links) => new()
        {
            Uid = uid,
            ScriptName = name,
            Shape = 1,
            Position = box.Centre,
            Orientation = box.Orientation,
            // the model keeps a box as height, width, depth
            Size = new Vector3(box.Size.Y, box.Size.X, box.Size.Z),
            ResetsTimes = 1,
            Links = links,
        };

        private static string MarkerReason(ushort type) => type switch
        {
            3104 => "an instant-death volume (likely); Red Faction has no such trigger without events",
            3105 => "a damage volume (likely); Red Faction has no such trigger without events",
            3111 => "a mode-change volume of the stealth sections (likely)",
            3117 or 3118 => "a volume whose meaning is unknown",
            2012 => "a camera rig",
            _ => "a marker type with no Red Faction counterpart",
        };

        // ── Report ─────────────────────────────────────────────────────────

        private void Summarise(RfGeometry geometry)
        {
            static string N(int n, string one, string? many = null) => LvlReportBuilder.Count(n, one, many);
            _notes.Info(LvlConvertCodes.XffLevel, $"This is a Red Faction for N-Gage level, read at {scale.ToString("0.###", CultureInfo.InvariantCulture)} Red Faction metres per N-Gage unit (the triangles turned to face Red Faction's way). The compiled geometry is the level's meshes ({N(geometry.Faces.Length, "triangle")}, one room per area: {N(_areas.Count, "room")}); the brushes are an air brush per area and detail brushes for the rest ({N(_brushes.Count, "brush", "brushes")}).");
            if (_colouredCorners > 0)
                _notes.Info(LvlConvertCodes.VertexColors, "The N-Gage level is lit by colours baked into its corners; Red Faction has no such colours, so they were dropped. Add lights to the level and build its lighting in the editor.", LvlIssueKind.Dropped);
            if (_alphaFaces > 0)
                _notes.Info(LvlConvertCodes.Transparency, $"{N(_alphaFaces, "triangle")} use an alpha-blended material (glass, decals); they are alpha faces. Their see-through amount came from the baked corner alpha, which Red Faction does not have: the texture's own alpha decides.", LvlIssueKind.Approximated);
            if (_additiveFaces > 0)
                _notes.Info(LvlConvertCodes.Transparency, $"{N(_additiveFaces, "triangle")} use an additive material (lasers, glows); Red Faction has no additive faces, so they are full-bright faces.", LvlIssueKind.Approximated);
            if (_darkeningFaces > 0)
                _notes.Info(LvlConvertCodes.Transparency, $"{N(_darkeningFaces, "triangle")} use a darkening material (zero, inverse source colour: the level-end marks and broken-wall overlays); they are alpha faces, which do not darken in Red Faction.", LvlIssueKind.Approximated);
            if (_otherBlendFaces > 0)
                _notes.Info(LvlConvertCodes.Transparency, $"{N(_otherBlendFaces, "triangle")} use material blends of unknown meaning ({string.Join(", ", _otherBlends)}); they are ordinary faces.", LvlIssueKind.Approximated);
            if (_untexturedFaces > 0)
                _notes.Info(LvlConvertCodes.Untextured, $"{N(_untexturedFaces, "triangle")} have no texture (no UVs in the N-Gage file).", LvlIssueKind.Approximated);
            if (_placedGeometry.Count > 0)
                _notes.Info(LvlConvertCodes.XffObjects, $"Doors, buttons, walls and props the level places as their own files are static geometry: their files have no animation (the N-Gage game moves them in code), so they cannot become movers. {string.Join(", ", _placedGeometry.Select(kv => $"{kv.Key} ({kv.Value})"))}.", LvlIssueKind.Approximated);
            if (_missing.Count > 0)
                _notes.Warning(LvlConvertCodes.XffLevel, $"{N(_missing.Count, "placed file was", "placed files were")} not found beside the level, so {(_missing.Count == 1 ? "its" : "their")} geometry is missing: {string.Join(", ", _missing.Take(10))}{(_missing.Count > 10 ? " ..." : "")}. Open the level from its .cfl (or the folder it was extracted to).", LvlIssueKind.Dropped);
            var mapped = _entities.Where(e => !e.StartHidden).Cast<RfPlacedObject>().Concat(_items).Concat(_clutter)
                .GroupBy(o => o.ClassName, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}").ToList();
            if (mapped.Count > 0 || _playerStart is not null || _respawns.Count > 0)
                _notes.Info(LvlConvertCodes.XffObjects, "Placed objects mapped by the N-Gage object table (the classes are drafts the author confirms): "
                    + string.Join(", ", mapped) + (_playerStart is not null ? "; the player start" : "") + (_respawns.Count > 0 ? $"; {N(_respawns.Count, "multiplayer respawn point")} (3103)" : "") + ".", LvlIssueKind.Approximated);
            if (_unmapped.Count > 0)
                _notes.Warning(LvlConvertCodes.XffObjects, "Not converted (no Red Faction counterpart): "
                    + string.Join("; ", _unmapped.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} x{kv.Value.Count}: {_unmappedReason[kv.Key]}")) + ".", LvlIssueKind.Dropped);
            int sprites = _scene.Nodes.Count(n => n.Chunk is XffSprite);
            int emitters = _scene.Nodes.Count(n => n.Chunk is XffEmitter);
            int lights = _scene.Nodes.Count(n => n.Chunk is XffLight);
            if (sprites + emitters + lights > 0)
            {
                var parts = new List<string>();
                if (sprites > 0) parts.Add($"{N(sprites, "flare sprite")} (where the lamps are: good places for lights)");
                if (emitters > 0) parts.Add(N(emitters, "particle emitter"));
                if (lights > 0) parts.Add($"{N(lights, "light")} (fields not known)");
                _notes.Info(LvlConvertCodes.XffObjects, $"Not converted: {string.Join(", ", parts)}.", LvlIssueKind.Dropped);
            }
        }
    }
}
