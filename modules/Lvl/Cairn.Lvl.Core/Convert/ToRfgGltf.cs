using System.Text.Json.Nodes;
using Cairn.Formats;
using Cairn.Formats.Gltf;
using Cairn.Lvl.Model;
using Cairn.Rfa.Interchange;

namespace Cairn.Lvl.Conversion;

public static partial class LvlConverter
{
    /// <summary>
    /// glTF to a group file: one brush per mesh node (nodes that share an <c>rf_brush_uid</c> make one brush), placed
    /// by the node's position and rotation with any scale baked into the vertices. Cairn's own extras restore brush
    /// flags, life, state, uids, face flags, smoothing, scrolling, groups, moving data and every object; REDUX's keys
    /// (<c>rf_brush_uid</c>, <c>rf_texture</c>) are read too. Meshes without brush data go through the pre-flight
    /// check (closed meshes become solid brushes, others detail brushes).
    /// </summary>
    /// <param name="document">The glTF document (images are not needed).</param>
    /// <param name="sourceName">The source file name (used for the main group's name and the output name).</param>
    /// <param name="options">What meshes become; null uses the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static LvlConversion GltfToRfg(GltfDocument document, string sourceName, RfgImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        return new RfgFromGltf(document, sourceName, options ?? new RfgImportOptions(), cancellationToken).Run();
    }
}

/// <summary>glTF to group file (see <see cref="LvlConverter.GltfToRfg"/>).</summary>
internal sealed class RfgFromGltf(GltfDocument doc, string sourceName, RfgImportOptions options, CancellationToken ct)
{
    private readonly LvlReportBuilder _report = new();
    private readonly Matrix4x4[] _world = doc.ComputeWorldMatrices();
    private readonly int[] _parents = doc.ComputeParents();
    private readonly HashSet<GltfMaterial> _guessedMaterials = new(ReferenceEqualityComparer.Instance);
    private int _editorOnly, _lodsClamped, _otherLodNodes, _meshOnlyNodes;
    private int _nonTriangles, _badPrimitives, _baked, _mirrored, _doubleSided, _objectsMoved, _lightsMade, _filtered, _noFaceData, _polygonFallbacks;
    private readonly HashSet<string> _unknownTypes = new(StringComparer.Ordinal);

    /// <summary>A transform from a node's own glTF space to RF world space, split the way a brush stores it.</summary>
    private readonly record struct Placement(Vector3 Position, RfMatrix Orientation, Matrix4x4 Bake, bool Mirrors);

    public LvlConversion Run()
    {
        string main = options.GroupName ?? RootName() ?? Path.GetFileNameWithoutExtension(sourceName);
        var asm = new RfgFileAssembler(main, _report);
        asm.Reserve(WantedUids());
        var builder = new RfgBrushBuilder(options.BrushKind, _report);

        // Brushes: mesh nodes, merged by brush uid when several nodes carry the same one. A mesh glTF holds each
        // submesh once per level of detail; only the chosen one becomes a brush, as from the .v3m.
        var otherLods = OtherLods();
        var brushes = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var order = new List<string>();
        for (int i = 0; i < doc.Nodes.Count; i++)
        {
            var node = doc.Nodes[i];
            if (node.Mesh is not { } m || m < 0 || m >= doc.Meshes.Count || otherLods.Contains(i)) continue;
            string key = BrushUid(node) is { } uid ? $"uid {uid}" : $"node {i}";
            if (!brushes.TryGetValue(key, out var list))
            {
                brushes[key] = list = [];
                order.Add(key);
            }
            list.Add(i);
        }
        foreach (string key in order)
        {
            ct.ThrowIfCancellationRequested();
            var nodes = brushes[key];
            var source = Brush(nodes);
            if (source is null) continue;
            var brush = builder.Build(source, asm.Uid(source.Uid));
            if (brush is null) continue;
            int group = Group(asm, nodes[0]);
            asm.AddBrush(group, brush);
        }

        // Objects: nodes Cairn wrote with their fields, and glTF lights.
        for (int i = 0; i < doc.Nodes.Count; i++)
        {
            if (Object(i) is not { } obj) continue;
            asm.AddObject(Group(asm, i), obj with { Uid = asm.Uid(obj.Uid >= 0 ? obj.Uid : null) });
        }
        builder.Summarise();
        Summarise();

        var file = asm.Build(Path.GetFileNameWithoutExtension(sourceName) + ".rfg", options.Version);
        if (file.Groups.All(g => g.Brushes.Length == 0 && g.Objects.Count == 0 && g.Moving is null))
        {
            _report.Error(LvlConvertCodes.Empty, "The glTF file has no mesh or object a group file can hold.", LvlIssueKind.Dropped);
            return new LvlConversion(LvlConvertTarget.Rfg, _report.Build(), null, null, null);
        }
        return RfgOutput.Finish(file, options.Version, _report);
    }

    // ── Nodes ──────────────────────────────────────────────────────────────

    private static string? TypeOf(GltfNode node) => GltfExtras.TryGetString(node.Extras, LvlGltfKeys.Type, out string t) ? t : null;

    private string? RootName()
    {
        foreach (var node in doc.Nodes)
            if (TypeOf(node) is "level" or "group_file" && !string.IsNullOrWhiteSpace(node.Name)) return node.Name;
        return null;
    }

    private int? BrushUid(GltfNode node)
    {
        if (GltfExtras.TryGetInt(node.Extras, LvlGltfKeys.BrushUid, out int uid)) return uid;
        if (node.Mesh is { } m && m >= 0 && m < doc.Meshes.Count)
            foreach (var p in doc.Meshes[m].Primitives)
                if (GltfExtras.TryGetInt(p.Extras, LvlGltfKeys.BrushUid, out uid)) return uid;
        return null;
    }

    /// <summary>
    /// The mesh nodes of a mesh glTF (nodes with <c>rf_lod_index</c>) that are not the chosen level of detail of
    /// their submesh. The chosen one is <see cref="RfgImportOptions.Lod"/>, or the submesh's last one below it when
    /// the file has fewer (as from the .v3m); nodes without a LOD index are always kept.
    /// </summary>
    private HashSet<int> OtherLods()
    {
        var bySubmesh = new Dictionary<string, List<(int Node, int Lod)>>(StringComparer.Ordinal);
        for (int i = 0; i < doc.Nodes.Count; i++)
        {
            var node = doc.Nodes[i];
            if (node.Mesh is null || !GltfExtras.TryGetInt(node.Extras, GltfExtras.LodIndex, out int lod) || lod < 0) continue;
            string key = GltfExtras.TryGetInt(node.Extras, GltfExtras.SubmeshIndex, out int sub) ? $"submesh {sub}"
                : node.Name is { } n && n.LastIndexOf("_LOD", StringComparison.Ordinal) is int at and > 0 ? $"name {n[..at]}" : $"node {i}";
            if (!bySubmesh.TryGetValue(key, out var list)) bySubmesh[key] = list = [];
            list.Add((i, lod));
        }
        var others = new HashSet<int>();
        foreach (var list in bySubmesh.Values)
        {
            int below = list.Where(e => e.Lod <= options.Lod).Select(e => e.Lod).DefaultIfEmpty(-1).Max();
            int chosen = below >= 0 ? below : list.Min(e => e.Lod);
            if (chosen != options.Lod) _lodsClamped++;
            foreach (var (node, lod) in list)
            {
                if (lod == chosen) continue;
                others.Add(node);
                _otherLodNodes++;
            }
        }
        return others;
    }

    private IEnumerable<int> WantedUids()
    {
        foreach (var node in doc.Nodes)
        {
            if (BrushUid(node) is { } b) yield return b;
            if (GltfExtras.TryGetInt(node.Extras, LvlGltfKeys.Uid, out int u)) yield return u;
            if (GltfExtras.Get(node.Extras, LvlGltfKeys.Moving) is JsonObject moving && moving["keyframes"] is JsonArray keys)
                foreach (var k in keys)
                    if (k is JsonObject ko && GltfExtras.TryGetInt(ko, "uid", out int ku)) yield return ku;
        }
    }

    /// <summary>The group a node belongs to: its nearest ancestor that is a group node, or the main group.</summary>
    private int Group(RfgFileAssembler asm, int node)
    {
        for (int p = _parents[node], guard = 0; p >= 0 && guard < doc.Nodes.Count; p = _parents[p], guard++)
        {
            var parent = doc.Nodes[p];
            if (TypeOf(parent) != "group") continue;
            string name = GltfExtras.TryGetString(parent.Extras, LvlGltfKeys.GroupName, out string n) && n.Length > 0 ? n : parent.Name ?? $"group {p}";
            RfMovingData? moving = null;
            if (GltfExtras.Get(parent.Extras, LvlGltfKeys.Moving) is JsonObject m)
            {
                try
                {
                    moving = LvlExtras.FromJson<RfMovingData>(m);
                }
                catch (FormatException)
                {
                    _unknownTypes.Add("moving group data");
                }
            }
            return asm.Group(name, moving);
        }
        return 0;
    }

    /// <summary>A node's world transform in RF space: mirror into RF on both sides of the glTF matrix.</summary>
    private Matrix4x4 RfWorld(int node)
    {
        var mirror = Matrix4x4.CreateScale(-1, 1, 1);
        return mirror * _world[node] * mirror;
    }

    /// <summary>Splits an RF-space transform into a brush position and rotation, with scale (or anything else) left to bake into the vertices.</summary>
    private Placement Place(Matrix4x4 a)
    {
        var linear = a with { M41 = 0, M42 = 0, M43 = 0 };
        var translation = a.Translation;
        bool mirrors = linear.GetDeterminant() < 0;
        if (!mirrors && Matrix4x4.Decompose(linear, out var scale, out var rotation, out _))
        {
            var r = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
            var rebuilt = Matrix4x4.CreateScale(scale) * r;
            if (Close(rebuilt, linear, 1e-4f))
            {
                var orient = new RfMatrix(new Vector3(r.M11, r.M12, r.M13), new Vector3(r.M21, r.M22, r.M23), new Vector3(r.M31, r.M32, r.M33));
                return new Placement(translation, orient, Matrix4x4.CreateScale(scale), false);
            }
        }
        // Shear or a mirror: no rotation can hold it, so all of it goes into the vertices.
        return new Placement(translation, RfMatrix.Identity, linear, mirrors);
    }

    private static bool Close(Matrix4x4 a, Matrix4x4 b, float tolerance)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if (MathF.Abs(a[r, c] - b[r, c]) > tolerance) return false;
        return true;
    }

    private static bool IsIdentity(Matrix4x4 m) => Close(m, Matrix4x4.Identity, 1e-6f);

    // ── Brushes ────────────────────────────────────────────────────────────

    private RfgSourceBrush? Brush(List<int> nodes)
    {
        var first = doc.Nodes[nodes[0]];
        var x = first.Extras;
        var source = new RfgSourceBrush { Name = first.Name ?? $"node {nodes[0]}", Uid = BrushUid(first) };
        if (GltfExtras.TryGetNumber(x, LvlGltfKeys.SolidFlags, out double solid)) source.SolidFlags = (RfSolidFlags)(uint)solid;
        if (GltfExtras.TryGetInt(x, LvlGltfKeys.Life, out int life)) source.Life = life;
        if (GltfExtras.TryGetInt(x, LvlGltfKeys.State, out int state)) source.State = state;
        if (GltfExtras.Get(x, LvlGltfKeys.Alpine) is JsonObject alpine)
        {
            try
            {
                source.Alpine = LvlExtras.FromJson<RfAlpineBrushData>(alpine);
            }
            catch (FormatException)
            {
                _unknownTypes.Add("Alpine brush data");
            }
        }

        // Where the brush sits. Cairn's extras give the exact stored values while the node still has them.
        var stored = StoredPlacement(first);
        bool applied = GltfExtras.TryGetBool(x, LvlGltfKeys.TransformApplied, out bool a) && a;
        var worlds = nodes.Select(RfWorld).ToList();
        Func<Vector3, Vector3>[] toLocal = new Func<Vector3, Vector3>[nodes.Count];
        bool mirrors = false;
        if (nodes.Count == 1 && !applied && (stored is not null || !IsIdentity(worlds[0])))
        {
            var place = Place(worlds[0]);
            if (stored is { } s && Matches(place, s))
            {
                source.Position = s.Position;
                source.Orientation = s.Orientation;
                toLocal[0] = p => p;
            }
            else
            {
                source.Position = place.Position;
                source.Orientation = place.Orientation;
                var bake = place.Bake;
                if (!IsIdentity(bake)) _baked++;
                toLocal[0] = p => Vector3.Transform(p, bake);
                mirrors = place.Mirrors;
            }
        }
        else
        {
            // Vertices in world space (applied transforms, REDUX output, merged nodes): the brush keeps its stored
            // placement when it has one, else it sits at the centre of its bounds.
            var all = new List<Vector3>();
            for (int k = 0; k < nodes.Count; k++)
                foreach (var p in Positions(nodes[k])) all.Add(Vector3.Transform(p, worlds[k]));
            var pos = stored?.Position ?? Center(all);
            var orient = stored?.Orientation ?? RfMatrix.Identity;
            if (stored is not null && nodes.Count == 1 && !IsIdentity(worlds[0])) { pos = Center(all); orient = RfMatrix.Identity; }
            source.Position = pos;
            source.Orientation = orient;
            for (int k = 0; k < nodes.Count; k++)
            {
                var w = worlds[k];
                toLocal[k] = p => Unrotate(orient, Vector3.Transform(p, w) - pos);
                if (w.GetDeterminant() < 0) mirrors = true;
            }
        }
        if (mirrors) _mirrored++;

        for (int k = 0; k < nodes.Count; k++) AddFaces(source, doc.Nodes[nodes[k]], toLocal[k], mirrors);
        return source.Faces.Count == 0 ? null : source;
    }

    private static Vector3 Unrotate(RfMatrix m, Vector3 d) => new(Vector3.Dot(d, m.Right), Vector3.Dot(d, m.Up), Vector3.Dot(d, m.Forward));

    private static Vector3 Center(List<Vector3> points)
    {
        if (points.Count == 0) return Vector3.Zero;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in points)
        {
            if (!RfgBrushBuilder.Finite(p)) continue;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return min.X <= max.X ? (min + max) / 2 : Vector3.Zero;
    }

    private static (Vector3 Position, RfMatrix Orientation)? StoredPlacement(GltfNode node)
    {
        try
        {
            if (GltfExtras.Get(node.Extras, LvlGltfKeys.Position) is { } p && GltfExtras.Get(node.Extras, LvlGltfKeys.Orientation) is { } o)
                return (LvlExtras.ReadVector3(p), LvlExtras.ReadMatrix(o));
        }
        catch (FormatException)
        {
        }
        return null;
    }

    /// <summary>True when the node still sits where Cairn's extras say (it was not moved, turned or scaled since).</summary>
    private static bool Matches(Placement place, (Vector3 Position, RfMatrix Orientation) stored)
    {
        const float tolerance = 1e-3f;
        if (!IsIdentity(place.Bake) && !Close(place.Bake, Matrix4x4.Identity, 1e-4f)) return false;
        float scale = Math.Max(1f, stored.Position.Length());
        return Vector3.Distance(place.Position, stored.Position) <= tolerance * scale
            && Vector3.Distance(place.Orientation.Right, stored.Orientation.Right) <= tolerance
            && Vector3.Distance(place.Orientation.Up, stored.Orientation.Up) <= tolerance
            && Vector3.Distance(place.Orientation.Forward, stored.Orientation.Forward) <= tolerance;
    }

    /// <summary>The node's positions in RF axes (its own space).</summary>
    private IEnumerable<Vector3> Positions(int node)
    {
        var n = doc.Nodes[node];
        if (n.Mesh is not { } m || m < 0 || m >= doc.Meshes.Count) yield break;
        foreach (var prim in doc.Meshes[m].Primitives)
        {
            if (!prim.Attributes.TryGetValue("POSITION", out int pos)) continue;
            Vector3[] positions;
            try
            {
                positions = GltfAccessorReader.ReadVector3(doc, pos, sourceName);
            }
            catch (AssetFormatException)
            {
                continue;
            }
            foreach (var p in positions) yield return GltfSpace.FromGltf(p);
        }
    }

    private void AddFaces(RfgSourceBrush brush, GltfNode node, Func<Vector3, Vector3> toLocal, bool mirrors)
    {
        var mesh = doc.Meshes[node.Mesh!.Value];
        foreach (var prim in mesh.Primitives)
        {
            if (prim.Mode is { } mode && mode != 4)
            {
                _nonTriangles++;
                continue;
            }
            Vector3[] positions;
            Vector3[]? normals = null;
            Vector2[]? uvs = null;
            int[] indices;
            try
            {
                if (!prim.Attributes.TryGetValue("POSITION", out int pos)) throw new AssetFormatException("no positions");
                positions = GltfAccessorReader.ReadVector3(doc, pos, sourceName);
                if (prim.Attributes.TryGetValue("NORMAL", out int nrm)) normals = GltfAccessorReader.ReadVector3(doc, nrm, sourceName);
                if (prim.Attributes.TryGetValue("TEXCOORD_0", out int uv)) uvs = GltfAccessorReader.ReadVector2(doc, uv, sourceName);
                indices = prim.Indices is { } ix ? GltfAccessorReader.ReadInts(doc, ix, sourceName) : [.. Enumerable.Range(0, positions.Length)];
                if (normals is not null && normals.Length != positions.Length) normals = null;
                if (uvs is not null && uvs.Length != positions.Length) uvs = null;
                if (indices.Length % 3 != 0 || indices.Any(i => (uint)i >= (uint)positions.Length)) throw new AssetFormatException("bad indices");
            }
            catch (AssetFormatException)
            {
                _badPrimitives++;
                continue;
            }

            // Corners in RF axes: X mirrored back, and the triangle winding turned back with it.
            var local = positions.Select(p => toLocal(GltfSpace.FromGltf(p))).ToArray();
            RfgSourceCorner Corner(int i) => new(local[i], uvs?[i] ?? Vector2.Zero, normals is null ? null : GltfSpace.FromGltf(normals[i]));
            var triangles = new List<(int A, int B, int C)>(indices.Length / 3);
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                // glTF (a, b, c) is RF (a, c, b); a mirrored node transform turns it over once more.
                triangles.Add(mirrors ? (indices[t], indices[t + 1], indices[t + 2]) : (indices[t], indices[t + 2], indices[t + 1]));
            }

            var px = prim.Extras;
            var material = prim.Material is { } mi && mi >= 0 && mi < doc.Materials.Count ? doc.Materials[mi] : null;
            string texture = Texture(px, material);
            bool unlit = material?.Extensions?.ContainsKey("KHR_materials_unlit") == true || GltfExtras.TryGetBool(material?.Extras, LvlGltfKeys.FullBright, out bool fb) && fb;
            bool doubleSided = GltfExtras.TryGetBool(px, LvlGltfKeys.DoubleSided, out bool ds) ? ds : material?.DoubleSided == true;

            var perFace = Ints(px, LvlGltfKeys.FaceTriangles);
            if (perFace is not null && perFace.Sum() == triangles.Count && perFace.All(n => n > 0))
            {
                var flags = Ints(px, LvlGltfKeys.FaceFlags);
                var raw = Numbers(px, LvlGltfKeys.FaceRawFlags);
                var smoothing = Numbers(px, LvlGltfKeys.FaceSmoothing);
                var ids = Ints(px, LvlGltfKeys.FaceIds);
                var scroll = Numbers(px, LvlGltfKeys.FaceScroll);
                int at = 0;
                for (int f = 0; f < perFace.Length; f++)
                {
                    var tris = triangles.GetRange(at, perFace[f]);
                    at += perFace[f];
                    var faceFlags = (RfFaceFlags)(flags is not null && f < flags.Length ? flags[f] & 0xFFFF : 0);
                    var template = new RfgSourceFace([], texture)
                    {
                        Flags = faceFlags,
                        RawFlags = raw is not null && f < raw.Length ? (uint)(long)raw[f] : null,
                        Smoothing = smoothing is not null && f < smoothing.Length ? (uint)(long)smoothing[f] : null,
                        FaceId = ids is not null && f < ids.Length ? ids[f] : -1,
                        Scroll = ScrollOf(scroll, f, faceFlags),
                    };
                    var loop = Polygon(tris);
                    if (loop is null)
                    {
                        _polygonFallbacks++;
                        foreach (var (a, b, c) in tris) brush.Faces.Add(template with { Corners = [Corner(a), Corner(b), Corner(c)] });
                    }
                    else
                    {
                        brush.Faces.Add(template with { Corners = [.. loop.Select(Corner)] });
                    }
                }
            }
            else
            {
                _noFaceData += triangles.Count;
                var flags = unlit ? RfFaceFlags.FullBright : RfFaceFlags.None;
                foreach (var (a, b, c) in triangles)
                {
                    brush.Faces.Add(new RfgSourceFace([Corner(a), Corner(b), Corner(c)], texture) { Flags = flags });
                    if (doubleSided) brush.Faces.Add(new RfgSourceFace([Corner(a), Corner(c), Corner(b)], texture) { Flags = flags });
                }
                if (doubleSided) _doubleSided += triangles.Count;
            }
        }
    }

    /// <summary>A face's scroll speed: faces that do not scroll are stored as zeros.</summary>
    private static Vector2? ScrollOf(double[]? scroll, int face, RfFaceFlags flags)
    {
        if (scroll is null || 2 * face + 1 >= scroll.Length) return null;
        var speed = new Vector2((float)scroll[2 * face], (float)scroll[2 * face + 1]);
        return (flags & RfFaceFlags.ScrollTexture) != 0 || speed != Vector2.Zero ? speed : null;
    }

    /// <summary>
    /// The polygon a face's triangles make: its boundary edges (those whose reverse no other triangle of the face
    /// has) chained into one loop. Null when they do not make exactly one simple loop.
    /// </summary>
    internal static List<int>? Polygon(List<(int A, int B, int C)> triangles)
    {
        if (triangles.Count == 1) return [triangles[0].A, triangles[0].B, triangles[0].C];
        var edges = new HashSet<(int, int)>();
        foreach (var (a, b, c) in triangles)
        {
            if (!edges.Add((a, b)) || !edges.Add((b, c)) || !edges.Add((c, a))) return null;
        }
        var next = new Dictionary<int, int>();
        foreach (var (from, to) in edges)
        {
            if (edges.Contains((to, from))) continue;
            if (!next.TryAdd(from, to)) return null;
        }
        if (next.Count < 3) return null;
        var loop = new List<int>(next.Count);
        // Ear clipping a convex polygon cuts the ear at its first corner first, as (last, first, second), so the
        // loop starts there and the corners come back in their stored order.
        int start = next.ContainsKey(triangles[0].B) ? triangles[0].B : next.Keys.First(), v = start;
        do
        {
            loop.Add(v);
            if (!next.TryGetValue(v, out v) || loop.Count > next.Count) return null;
        }
        while (v != start);
        return loop.Count == next.Count ? loop : null;
    }

    /// <summary>
    /// A primitive's RF texture: <c>rf_texture</c> on the primitive or its material (present but empty means
    /// untextured, as Cairn writes it), else a guess from the material or image name.
    /// </summary>
    private string Texture(JsonNode? primitiveExtras, GltfMaterial? material)
    {
        if (GltfExtras.TryGetString(primitiveExtras, LvlGltfKeys.Texture, out string t)) return t;
        if (material is null) return "";
        if (GltfExtras.TryGetString(material.Extras, LvlGltfKeys.Texture, out t)) return t;
        string? name = material.Name;
        if (name is not null && RfgFromObj.UntexturedMaterial(name) && material.PbrMetallicRoughness?.BaseColorTexture is null) return "";
        if (name is not null && LooksLikeTexture(name))
        {
            _guessedMaterials.Add(material);
            return RfTextureName(name);
        }
        if (material.PbrMetallicRoughness?.BaseColorTexture is { } info && info.Index >= 0 && info.Index < doc.Textures.Count
            && doc.Textures[info.Index].Source is { } src && src >= 0 && src < doc.Images.Count)
        {
            var image = doc.Images[src];
            string? file = image.Uri is { } uri && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(Uri.UnescapeDataString(uri)) : image.Name;
            if (!string.IsNullOrWhiteSpace(file))
            {
                _guessedMaterials.Add(material);
                return RfTextureName(file);
            }
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            _guessedMaterials.Add(material);
            return RfTextureName(name);
        }
        return "";
    }

    internal static bool LooksLikeTexture(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".tga" or ".vbm" or ".dds" or ".bmp" or ".png" or ".jpg" or ".jpeg";

    /// <summary>
    /// A texture name for RF from a material or image name: RF bitmaps are .tga (or .vbm/.dds), so a PNG or JPG
    /// name (what exporters write for a decoded copy) and a name without an extension become .tga.
    /// </summary>
    internal static string RfTextureName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".tga" or ".vbm" or ".dds" or ".bmp" => name,
        ".png" or ".jpg" or ".jpeg" => Path.GetFileNameWithoutExtension(name) + ".tga",
        _ => name + ".tga",
    };

    private static int[]? Ints(JsonNode? extras, string key) => Numbers(extras, key)?.Select(d => (int)(long)d).ToArray();

    private static double[]? Numbers(JsonNode? extras, string key)
    {
        if (GltfExtras.Get(extras, key) is not JsonArray a) return null;
        var result = new double[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            try
            {
                // Whole numbers keep every bit (smoothing groups use all 32); "NaN" and the infinities are strings.
                result[i] = GltfExtras.TryNumber(a[i], out double d) ? d : LvlExtras.ReadFloat(a[i]);
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return result;
    }

    // ── Objects ────────────────────────────────────────────────────────────

    private RfObject? Object(int index)
    {
        var node = doc.Nodes[index];
        string? type = TypeOf(node);
        if (type is null)
            return node.Mesh is null ? Light(index) : null;
        if (type is "level" or "group_file" or "group" or "brush" or "room" or "mover" or "keyframe" or "player_start") return null;
        // A mesh glTF's bones, prop points and collision spheres belong to the mesh, not to a brush.
        if (type is "bone" or "prop_point" or "collision_sphere")
        {
            _meshOnlyNodes++;
            return null;
        }
        // Lights that exist only in the editor have no list in a group file.
        if (GltfExtras.TryGetBool(node.Extras, LvlGltfKeys.EditorOnly, out bool editorOnly) && editorOnly)
        {
            _editorOnly++;
            return null;
        }
        if (LvlExtras.ObjectType(type) is not { } t || !typeof(RfObject).IsAssignableFrom(t))
        {
            _unknownTypes.Add(type);
            return null;
        }
        if (GltfExtras.Get(node.Extras, LvlGltfKeys.Fields) is not JsonObject fields)
        {
            _unknownTypes.Add(type + " without its fields");
            return null;
        }
        RfObject obj;
        try
        {
            obj = (RfObject)LvlExtras.FromJson(t, fields);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or NotSupportedException)
        {
            _unknownTypes.Add(type + " with unreadable fields");
            return null;
        }
        if ((options.Objects & LvlScene.KindOf(obj)) == 0)
        {
            _filtered++;
            return null;
        }
        return Moved(obj, index);
    }

    /// <summary>An object whose node was moved or turned since Cairn wrote it takes the node's placement.</summary>
    private RfObject Moved(RfObject obj, int index)
    {
        var place = Place(RfWorld(index));
        bool moved = false;
        if (RfgBrushBuilder.Finite(place.Position) && Vector3.Distance(place.Position, obj.Position) > 1e-3f * Math.Max(1f, obj.Position.Length()))
        {
            obj = obj with { Position = place.Position };
            moved = true;
        }
        var stored = LvlScene.OrientationOf(obj);
        if (stored is { } s && LvlScene.IsRotation(s) && doc.Nodes[index].Rotation is not null
            && (Vector3.Distance(s.Right, place.Orientation.Right) > 1e-3f || Vector3.Distance(s.Up, place.Orientation.Up) > 1e-3f || Vector3.Distance(s.Forward, place.Orientation.Forward) > 1e-3f))
        {
            var o = place.Orientation;
            obj = obj switch
            {
                RfPlacedObject p => p with { Orientation = o },
                RfRespawnPoint r => r with { Orientation = o },
                RfTrigger r => r with { Orientation = o },
                RfEvent e => e with { Orientation = o },
                RfNavPoint n => n with { Orientation = o },
                RfGeoRegion g => g with { Orientation = o },
                _ => obj,
            };
            moved = true;
        }
        if (moved) _objectsMoved++;
        return obj;
    }

    /// <summary>A glTF light (KHR_lights_punctual) on a node Cairn did not write: an RF light approximating it.</summary>
    private RfLight? Light(int index)
    {
        var node = doc.Nodes[index];
        if (node.Extensions?["KHR_lights_punctual"] is not JsonObject ext || !GltfExtras.TryGetInt(ext, "light", out int li)) return null;
        // Cairn writes a spot light's glTF light on a child of the light's node; the parent already is the light.
        if (_parents[index] >= 0 && TypeOf(doc.Nodes[_parents[index]]) is not null) return null;
        if (doc.Extensions?["KHR_lights_punctual"]?["lights"] is not JsonArray lights || li < 0 || li >= lights.Count || lights[li] is not JsonObject def) return null;
        if ((options.Objects & LvlObjectKinds.Lights) == 0)
        {
            _filtered++;
            return null;
        }
        string type = GltfExtras.TryGetString(def, "type", out string tt) ? tt : "point";
        float[] color = GltfExtras.TryGetFloats(def, "color", 3, out var c) ? c : [1, 1, 1];
        float range = GltfExtras.TryGetNumber(def, "range", out double r) && r > 0 ? (float)r : 10f;
        byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        var w = RfWorld(index);
        // A glTF light shines down its node's -Z; an RF spot light shines along its forward row.
        var forward = Vector3.Normalize(-new Vector3(w.M31, w.M32, w.M33));
        var up = new Vector3(w.M21, w.M22, w.M23);
        up = Vector3.Normalize(up - Vector3.Dot(up, forward) * forward);
        if (!RfgBrushBuilder.Finite(forward) || !RfgBrushBuilder.Finite(up)) (forward, up) = (Vector3.UnitZ, Vector3.UnitY);
        var orient = new RfMatrix(Vector3.Cross(up, forward), up, forward);
        bool spot = type == "spot";
        float outer = 45f, inner = 0f;
        if (spot && def["spot"] is JsonObject s)
        {
            if (GltfExtras.TryGetNumber(s, "outerConeAngle", out double o)) outer = (float)(o * 180 / Math.PI);
            if (GltfExtras.TryGetNumber(s, "innerConeAngle", out double i)) inner = (float)(i * 180 / Math.PI);
        }
        _lightsMade++;
        return new RfLight
        {
            Uid = -1,
            ClassName = "Light",
            Position = w.Translation,
            Orientation = orient,
            Flags = (RfLightFlags)(0x08u | ((spot ? 2u : 1u) << 4)),
            Color = new Formats.Rfl.RflColor(Byte(color[0]), Byte(color[1]), Byte(color[2]), 255),
            Range = range,
            Fov = spot ? outer * 2 : 0,
            FovDropoff = spot ? Math.Max(0, (outer - inner) * 2) : 0,
            OnIntensity = 1,
        };
    }

    // ── Report ─────────────────────────────────────────────────────────────

    private void Summarise()
    {
        static string N(int n, string one, string? many = null) => LvlReportBuilder.Count(n, one, many);
        if (_noFaceData > 0)
            _report.Info(LvlRfgCodes.FaceData, $"{N(_noFaceData, "triangle")} came without Cairn's face data; each became a face with no face flags (full bright where the material is unlit).", LvlIssueKind.Approximated);
        if (_polygonFallbacks > 0)
            _report.Info(LvlRfgCodes.FaceData, $"{N(_polygonFallbacks, "face")} could not be rebuilt as one polygon from their triangles (the mesh was edited); their triangles became faces.", LvlIssueKind.Split);
        if (_guessedMaterials.Count > 0)
            _report.Info(LvlRfgCodes.TextureNames, $"Texture names of {N(_guessedMaterials.Count, "material")} come from the material or image name (no rf_texture extra); PNG and JPG names became .tga. Check them in RED.", LvlIssueKind.Approximated);
        if (_otherLodNodes > 0)
            _report.Info(LvlRfgCodes.FaceData, $"The mesh's other levels of detail were left out ({N(_otherLodNodes, "mesh node")}); each submesh became one brush from LOD {options.Lod}"
                + (_lodsClamped > 0 ? $", or its nearest one for {N(_lodsClamped, "submesh", "submeshes")} without it." : "."), LvlIssueKind.Dropped);
        else if (_lodsClamped > 0)
            _report.Info(LvlRfgCodes.FaceData, $"{N(_lodsClamped, "submesh", "submeshes")} have no LOD {options.Lod} in the glTF file; the nearest one was used.", LvlIssueKind.Approximated);
        if (_meshOnlyNodes > 0)
            _report.Info(LvlRfgCodes.FaceData, $"{N(_meshOnlyNodes, "bone, prop point or collision sphere", "bones, prop points and collision spheres")} of the mesh are not part of a brush and were left out.", LvlIssueKind.Dropped);
        if (_baked > 0)
            _report.Info(LvlRfgCodes.TransformBaked, $"{N(_baked, "node")} had a scale in their transform; it was baked into the brush vertices (brushes have a position and a rotation only).", LvlIssueKind.Approximated);
        if (_mirrored > 0)
            _report.Info(LvlRfgCodes.TransformBaked, $"{N(_mirrored, "brush", "brushes")} had a mirrored or skewed transform; it was baked into the vertices and their faces turned to keep facing out.", LvlIssueKind.Approximated);
        if (_doubleSided > 0)
            _report.Info(LvlRfgCodes.DoubleSided, $"{N(_doubleSided, "double-sided triangle")} were written twice, once facing each way (brush faces are one-sided).", LvlIssueKind.Split);
        if (_nonTriangles > 0)
            _report.Warning(LvlConvertCodes.ShortFaces, $"{N(_nonTriangles, "primitive")} draw lines, points or strips rather than triangles and were left out.", LvlIssueKind.Dropped);
        if (_badPrimitives > 0)
            _report.Warning(LvlConvertCodes.BadVertexFaces, $"{N(_badPrimitives, "primitive")} could not be read (missing or damaged positions or indices) and were left out.", LvlIssueKind.Dropped);
        if (_objectsMoved > 0)
            _report.Info(LvlConvertCodes.Objects, $"{N(_objectsMoved, "object")} were moved or turned in the glTF file; they take their node's placement.");
        if (_lightsMade > 0)
            _report.Info(LvlConvertCodes.Lights, $"{N(_lightsMade, "glTF light")} became RF lights (colour, range and cone kept; brightness and flags are RED's defaults).", LvlIssueKind.Approximated);
        if (_filtered > 0)
            _report.Info(LvlConvertCodes.ObjectsDropped, $"{N(_filtered, "object")} left out by the object filter.", LvlIssueKind.Dropped);
        if (_unknownTypes.Count > 0)
            _report.Warning(LvlConvertCodes.ObjectsDropped, $"Nodes of these kinds could not be read and were left out: {string.Join(", ", _unknownTypes.Order(StringComparer.Ordinal))}.", LvlIssueKind.Dropped);
        var notInGroups = new List<string>();
        if (doc.Nodes.Any(n => TypeOf(n) == "player_start")) notInGroups.Add("the player start");
        if (_editorOnly > 0) notInGroups.Add(N(_editorOnly, "editor-only light"));
        if (notInGroups.Count > 0)
            _report.Info(LvlRfgCodes.NotInGroups, $"A group file has no place for {string.Join(" and ", notInGroups)}; they are not written.", LvlIssueKind.Dropped);
    }
}
