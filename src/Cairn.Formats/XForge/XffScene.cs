using System.Numerics;

namespace Cairn.Formats.XForge;

/// <summary>A reference that does not lead where it should: to no chunk at all, or to a chunk of the wrong type.</summary>
/// <param name="FromId">The id of the chunk holding the reference.</param>
/// <param name="FromType">That chunk's type.</param>
/// <param name="Field">The reference's field ("parent", "submesh 2 material").</param>
/// <param name="TargetId">The id referred to.</param>
/// <param name="Problem">What is wrong ("no chunk has this id", "is a texture, not a vertex buffer").</param>
public sealed record XffReferenceProblem(uint FromId, ushort FromType, string Field, uint TargetId, string Problem)
{
    /// <inheritdoc/>
    public override string ToString() => $"{XffChunkTypes.Describe(FromType)} #{FromId} {Field} → #{TargetId}: {Problem}";
}

/// <summary>
/// One node of the scene tree: every chunk with a parent (group and object nodes, helpers, cameras, lights, emitters,
/// sprites, collisions, animation tracks, prefabs and markers), with its transforms, meshes and properties.
/// </summary>
public sealed class XffSceneNode
{
    internal readonly List<XffSceneNode> ChildList = [];
    internal readonly List<XffSceneMesh> MeshList = [];
    internal readonly List<XffProperty> PropertyList = [];

    internal XffSceneNode(int index, XffChunk chunk, IXffTreeChunk tree, XffTransform local)
    {
        Index = index;
        Chunk = chunk;
        Name = tree.Name;
        ParentId = tree.Parent;
        Local = local;
        LocalMatrix = XffSpace.LocalMatrix(local);
    }

    /// <summary>The node's place in <see cref="XffScene.Nodes"/> (file order).</summary>
    public int Index { get; }
    /// <summary>The chunk: <see cref="XffNode"/>, <see cref="XffHelper"/>, <see cref="XffAnimationTrack"/>, <see cref="XffPrefab"/>, ...</summary>
    public XffChunk Chunk { get; }
    /// <summary>The chunk id.</summary>
    public uint Id => Chunk.Id;
    /// <summary>The chunk type.</summary>
    public ushort Type => Chunk.Type;
    /// <summary>The node's name.</summary>
    public string Name { get; }
    /// <summary>The parent id as stored.</summary>
    public uint ParentId { get; }
    /// <summary>The parent node, or null for a root (and for a parent that is missing or would close a cycle: <see cref="XffScene.References"/> says so).</summary>
    public XffSceneNode? Parent { get; internal set; }
    /// <summary>The child nodes in file order.</summary>
    public IReadOnlyList<XffSceneNode> Children => ChildList;
    /// <summary>
    /// The local transform at rest: the stored TRS; a helper's position; for a track its first key (scale 1); identity
    /// for nodes that store none (emitters, sprites, collisions that refer to a mesh).
    /// </summary>
    public XffTransform Local { get; }
    /// <summary><see cref="Local"/> as a matrix.</summary>
    public Matrix4x4 LocalMatrix { get; }
    /// <summary>The world matrix at rest (every track at its first key).</summary>
    public Matrix4x4 WorldMatrix { get; internal set; } = Matrix4x4.Identity;
    /// <summary>The meshes of this node (usually none or one).</summary>
    public IReadOnlyList<XffSceneMesh> Meshes => MeshList;
    /// <summary>The node's properties (1009) in file order.</summary>
    public IReadOnlyList<XffProperty> Properties => PropertyList;
    /// <summary>The track when this node is an animation track (1005), else null.</summary>
    public XffSceneTrack? AsTrack { get; internal set; }
    /// <summary>The track this node hangs from directly (its parent is a track), else null: the track that animates it.</summary>
    public XffSceneTrack? Track => Parent?.AsTrack;
    /// <summary>True when a track is above this node in the tree, so it moves when the tracks play.</summary>
    public bool IsAnimated { get; internal set; }
    /// <summary>True for a game-object marker (type 2000 and up).</summary>
    public bool IsMarker => XffChunkTypes.IsMarker(Type);
    /// <summary>The prefab record when this node is one, else null.</summary>
    public XffPrefab? Prefab => Chunk as XffPrefab;
    /// <summary>True when one of <see cref="Properties"/> has the text <paramref name="text"/> exactly.</summary>
    public bool HasProperty(string text) => PropertyList.Any(p => p.Text == text);

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({XffChunkTypes.Describe(Type)} #{Id})";
}

/// <summary>An animation track of the scene: the track node, the node it animates, and its keys and timing.</summary>
public sealed class XffSceneTrack
{
    internal XffSceneTrack(XffSceneNode node, XffAnimationTrack chunk)
    {
        Node = node;
        Chunk = chunk;
    }

    /// <summary>The track's own node (its children move with it).</summary>
    public XffSceneNode Node { get; }
    /// <summary>The track record.</summary>
    public XffAnimationTrack Chunk { get; }
    /// <summary>The child the track is named after (<c>anim_&lt;name&gt;</c>), or null when no child has that name.</summary>
    public XffSceneNode? Target { get; internal set; }
    /// <summary>The track's name.</summary>
    public string Name => Chunk.Name;
    /// <summary>The animation played (the track's first; every known track has exactly one).</summary>
    public XffAnimation Animation => Chunk.First;
    /// <summary>Where the playing time starts, in milliseconds (may be negative).</summary>
    public int Start => Animation.Start;
    /// <summary>Where it ends or wraps, in milliseconds.</summary>
    public int End => Animation.End;
    /// <summary>Keys per second.</summary>
    public float Rate => Animation.Rate;
    /// <summary>The time between keys in whole milliseconds (<see cref="XffAnimation.Interval"/>: 33 at 30 keys per second).</summary>
    public int Interval => Animation.Interval;
    /// <summary>The flags (looping and the key channels).</summary>
    public XffAnimationFlags Flags => Animation.Flags;
    /// <summary>True when the playing time wraps into [<see cref="Start"/>, <see cref="End"/>]; otherwise it is clamped.</summary>
    public bool Loops => Flags.HasFlag(XffAnimationFlags.Loop);
    /// <summary>The keys.</summary>
    public IReadOnlyList<XffKey> Keys => Animation.Keys;

    /// <summary>The time of key <paramref name="index"/> in milliseconds: <c>index · <see cref="Interval"/></c>, counted from 0 (not from <see cref="Start"/>).</summary>
    public double KeyTime(int index) => (double)index * Interval;

    /// <summary>Key <paramref name="index"/> as a local transform.</summary>
    public XffTransform KeyTransform(int index) => new(Keys[index].Position, Keys[index].Rotation, Keys[index].Scale);

    /// <summary>
    /// The local transform at the playing time <paramref name="timeMs"/>, as the game evaluates it: a looping track
    /// wraps the time into [start, end] (<c>start + ((t − start) mod (end − start))</c>), another clamps it; key k is at
    /// <c>k · interval</c> ms from 0, a time before 0 holds key 0 and one past the last key holds the last. Between keys
    /// the position is interpolated linearly and the rotation spherically (the game's own rotation routine may be a
    /// normalised lerp; the keys are dense enough that both agree within a degree). A track without keys is the identity.
    /// </summary>
    public XffTransform Sample(double timeMs)
    {
        if (Keys.Count == 0) return XffTransform.Identity;
        if (Keys.Count == 1 || Interval <= 0 || !double.IsFinite(timeMs)) return KeyTransform(0);
        double t = timeMs;
        if (End > Start)
        {
            if (Loops)
            {
                double span = End - (double)Start;
                t = (t - Start) % span;
                if (t < 0) t += span;
                t += Start;
            }
            else t = Math.Clamp(t, Start, End);
        }
        if (t <= 0) return KeyTransform(0);
        double position = t / Interval;
        if (position >= Keys.Count - 1) return KeyTransform(Keys.Count - 1);
        int i = (int)Math.Floor(position);
        float f = (float)(position - i);
        var a = Keys[i];
        var b = Keys[i + 1];
        return new XffTransform(Vector3.Lerp(a.Position, b.Position, f),
            Quaternion.Slerp(XffSpace.SafeRotation(a.Rotation), XffSpace.SafeRotation(b.Rotation), f), Vector3.Lerp(a.Scale, b.Scale, f));
    }
}

/// <summary>A mesh of the scene: its instance record, the node it belongs to, and its resolved submeshes.</summary>
public sealed class XffSceneMesh
{
    internal XffSceneMesh(XffMeshInstance instance, XffSceneNode node, IReadOnlyList<XffSceneSubmesh> submeshes)
    {
        Instance = instance;
        Node = node;
        Submeshes = submeshes;
    }

    /// <summary>The mesh instance record.</summary>
    public XffMeshInstance Instance { get; }
    /// <summary>The node the mesh belongs to (its <see cref="XffSceneNode.WorldMatrix"/> places it).</summary>
    public XffSceneNode Node { get; }
    /// <summary>The mesh's name.</summary>
    public string Name => Instance.Name;
    /// <summary>The submeshes that resolved, in file order (one that did not is left out and reported in <see cref="XffScene.Problems"/>).</summary>
    public IReadOnlyList<XffSceneSubmesh> Submeshes { get; }
}

/// <summary>
/// One submesh with everything it refers to resolved: the vertex buffer's positions and normals (in the node's space),
/// a triangle list of indices into them, and per corner (index for index) a colour and a UV when the face attributes
/// hold them. XFF space as stored (<see cref="XffSpace"/> converts).
/// </summary>
public sealed class XffSceneSubmesh
{
    internal XffSceneSubmesh(int index, XffSubmesh record, XffVertexBuffer vertices, int[] indices, IReadOnlyList<XffColour> colours,
        IReadOnlyList<Vector2> uvs, XffMaterial? material, XffTexture? texture, XffTexture? reflection)
    {
        Index = index;
        Record = record;
        Vertices = vertices;
        Indices = indices;
        CornerColours = colours;
        CornerUvs = uvs;
        Material = material;
        Texture = texture;
        ReflectionTexture = reflection;
    }

    /// <summary>The submesh's place in its mesh instance.</summary>
    public int Index { get; }
    /// <summary>The submesh record.</summary>
    public XffSubmesh Record { get; }
    /// <summary>The vertex buffer.</summary>
    public XffVertexBuffer Vertices { get; }
    /// <summary>The positions (the whole vertex buffer, which submeshes may share).</summary>
    public IReadOnlyList<Vector3> Positions => Vertices.Positions;
    /// <summary>The normals (the whole vertex buffer; empty for a vertex format without normals, which no known file has).</summary>
    public IReadOnlyList<Vector3> Normals => Vertices.Normals;
    /// <summary>Three indices per triangle into <see cref="Positions"/>, every one in range.</summary>
    public IReadOnlyList<int> Indices { get; }
    /// <summary>The triangle count.</summary>
    public int TriangleCount => Indices.Count / 3;
    /// <summary>A colour per corner (as many as <see cref="Indices"/>), or empty.</summary>
    public IReadOnlyList<XffColour> CornerColours { get; }
    /// <summary>A UV per corner (as many as <see cref="Indices"/>), or empty.</summary>
    public IReadOnlyList<Vector2> CornerUvs { get; }
    /// <summary>The material, or null when the reference is "none" or does not resolve.</summary>
    public XffMaterial? Material { get; }
    /// <summary>The texture, or null for an untextured submesh (or a reference that does not resolve).</summary>
    public XffTexture? Texture { get; }
    /// <summary>The texture's file name, or null.</summary>
    public string? TextureName => Texture?.FileName;
    /// <summary>The reflection map texture, or null (none in all known files).</summary>
    public XffTexture? ReflectionTexture { get; }
}

/// <summary>
/// An X-Forge scene with its references resolved: the node tree with local and world transforms (at rest), meshes
/// with resolved submeshes, animation tracks, prefabs, properties per node and markers by type. Everything stays in
/// XFF space (<see cref="XffSpace"/> holds the conventions and the transform to Red Faction's). Building never throws
/// for a file <see cref="XffFile"/> read: what does not resolve is left out and reported (<see cref="References"/>,
/// <see cref="Problems"/>).
/// </summary>
public sealed class XffScene
{
    private readonly Dictionary<uint, XffSceneNode> _nodesById = [];
    private readonly List<XffSceneNode> _nodes = [];
    private readonly List<XffSceneMesh> _meshes = [];
    private readonly List<XffSceneTrack> _tracks = [];
    private readonly List<XffReferenceProblem> _references = [];
    private readonly List<string> _problems = [];

    private XffScene(XffFile file) => File = file;

    /// <summary>The file the scene was built from.</summary>
    public XffFile File { get; }
    /// <summary>Every node in file order.</summary>
    public IReadOnlyList<XffSceneNode> Nodes => _nodes;
    /// <summary>The nodes without a parent (normally the one <c>scene_root</c> or the file's own marker).</summary>
    public IReadOnlyList<XffSceneNode> Roots { get; private set; } = [];
    /// <summary>Every mesh in file order.</summary>
    public IReadOnlyList<XffSceneMesh> Meshes => _meshes;
    /// <summary>Every animation track in file order.</summary>
    public IReadOnlyList<XffSceneTrack> Tracks => _tracks;
    /// <summary>Every prefab node in file order.</summary>
    public IReadOnlyList<XffSceneNode> Prefabs { get; private set; } = [];
    /// <summary>Every marker node (type 2000 and up) in file order.</summary>
    public IReadOnlyList<XffSceneNode> Markers { get; private set; } = [];
    /// <summary>The materials in file order.</summary>
    public IReadOnlyList<XffMaterial> Materials { get; private set; } = [];
    /// <summary>The textures in file order.</summary>
    public IReadOnlyList<XffTexture> Textures { get; private set; } = [];
    /// <summary>References that lead to no chunk, or to a chunk of the wrong type.</summary>
    public IReadOnlyList<XffReferenceProblem> References => _references;
    /// <summary>Everything that was left out or is suspect, as text for the user (the reference problems and the file's diagnostics included).</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>The node with id <paramref name="id"/>, or null.</summary>
    public XffSceneNode? NodeById(uint id) => _nodesById.GetValueOrDefault(id);

    /// <summary>The markers of type <paramref name="type"/> in file order.</summary>
    public IReadOnlyList<XffSceneNode> MarkersOfType(ushort type) => [.. Markers.Where(m => m.Type == type)];

    /// <summary>The markers grouped by type, in ascending type order.</summary>
    public IReadOnlyList<IGrouping<ushort, XffSceneNode>> MarkersByType => [.. Markers.GroupBy(m => m.Type).OrderBy(g => g.Key)];

    /// <summary>The nodes that have a property with the text <paramref name="text"/> exactly ("room").</summary>
    public IReadOnlyList<XffSceneNode> NodesWithProperty(string text) => [.. _nodes.Where(n => n.HasProperty(text))];

    /// <summary>What the scene is (<see cref="XffClassifier"/>).</summary>
    public XffKind Kind => XffClassifier.Classify(this);

    /// <summary>Builds the scene of <paramref name="data"/> (reads the file first).</summary>
    /// <exception cref="AssetFormatException">The chunk framing is broken.</exception>
    public static XffScene Read(byte[] data, string name) => Build(XffFile.Read(data, name));

    /// <summary>Resolves <paramref name="file"/>'s references into a scene.</summary>
    public static XffScene Build(XffFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var scene = new XffScene(file);
        scene.BuildTree();
        scene.BuildMeshes();
        scene.AttachProperties();
        scene.CheckOtherReferences();
        scene.Materials = [.. file.All<XffMaterial>()];
        scene.Textures = [.. file.All<XffTexture>()];
        foreach (var diagnostic in file.Diagnostics) scene._problems.Add(diagnostic.ToString());
        return scene;
    }

    /// <summary>
    /// Every node's world matrix (by <see cref="XffSceneNode.Index"/>) with each track's local transform given by
    /// <paramref name="trackLocal"/> (for example <see cref="XffSceneTrack.Sample"/> at a time).
    /// </summary>
    public Matrix4x4[] WorldMatrices(Func<XffSceneTrack, XffTransform> trackLocal)
    {
        ArgumentNullException.ThrowIfNull(trackLocal);
        var world = new Matrix4x4[_nodes.Count];
        var done = new bool[_nodes.Count];
        foreach (var root in Roots) Walk(root, Matrix4x4.Identity);
        return world;

        void Walk(XffSceneNode start, Matrix4x4 parentWorld)
        {
            var stack = new Stack<(XffSceneNode Node, Matrix4x4 ParentWorld)>();
            stack.Push((start, parentWorld));
            while (stack.Count > 0)
            {
                var (node, parent) = stack.Pop();
                if (done[node.Index]) continue;
                done[node.Index] = true;
                var local = node.AsTrack is { } track ? XffSpace.LocalMatrix(trackLocal(track)) : node.LocalMatrix;
                var w = world[node.Index] = XffSpace.Compose(local, parent);
                for (int i = node.ChildList.Count - 1; i >= 0; i--) stack.Push((node.ChildList[i], w));
            }
        }
    }

    /// <summary>Every node's world matrix with the tracks sampled at <paramref name="timeMs"/> (<see cref="XffSceneTrack.Sample"/>).</summary>
    public Matrix4x4[] WorldMatricesAt(double timeMs) => WorldMatrices(t => t.Sample(timeMs));

    private void Reference(XffChunk from, string field, uint target, string problem) =>
        _references.Add(new XffReferenceProblem(from.Id, from.Type, field, target, problem));

    private static string Describe(XffChunk chunk) => $"is a {XffChunkTypes.Describe(chunk.Type)}";

    private void BuildTree()
    {
        foreach (var chunk in File.Chunks)
        {
            if (chunk is not IXffTreeChunk tree) continue;
            var local = chunk switch
            {
                XffNode n => n.Transform,
                XffHelper h => new XffTransform(h.Position, Quaternion.Identity, Vector3.One),
                XffCamera c => new XffTransform(c.Position, c.Rotation, Vector3.One),
                XffLight l => new XffTransform(l.Position, l.Rotation, Vector3.One),
                XffCollision { IsBox: true } c => c.Transform,
                XffPrefab p => p.Transform,
                XffAnimationTrack { Keys.Count: > 0 } t => new XffTransform(t.Keys[0].Position, t.Keys[0].Rotation, t.Keys[0].Scale),
                _ => XffTransform.Identity,
            };
            var node = new XffSceneNode(_nodes.Count, chunk, tree, local);
            _nodes.Add(node);
            if (!_nodesById.TryAdd(chunk.Id, node)) _nodes.RemoveAt(_nodes.Count - 1); // an id used twice: the file reported it; the first wins
            if (chunk is XffAnimationTrack track && ReferenceEquals(_nodesById[chunk.Id], node))
            {
                node.AsTrack = new XffSceneTrack(node, track);
                _tracks.Add(node.AsTrack);
            }
        }
        // parents (forward references are normal); a parent that would close a cycle is cut and reported
        var parents = new XffSceneNode?[_nodes.Count];
        foreach (var node in _nodes)
            if (node.ParentId != XffFile.NoReference) parents[node.Index] = _nodesById.GetValueOrDefault(node.ParentId);
        var cut = CycleCuts(parents);
        foreach (var node in _nodes)
        {
            if (node.ParentId == XffFile.NoReference) continue;
            if (parents[node.Index] is not { } parent)
            {
                Reference(node.Chunk, "parent", node.ParentId,
                    File.Find(node.ParentId) is { } other ? Describe(other) + ", not a node" : "no chunk has this id");
                continue;
            }
            if (cut[node.Index])
            {
                Reference(node.Chunk, "parent", node.ParentId, "would make the node its own ancestor; the node is treated as a root");
                continue;
            }
            node.Parent = parent;
            parent.ChildList.Add(node);
        }
        Roots = [.. _nodes.Where(n => n.Parent is null)];
        Prefabs = [.. _nodes.Where(n => n.Prefab is not null)];
        Markers = [.. _nodes.Where(n => n.IsMarker)];
        foreach (var track in _tracks)
        {
            track.Target = track.Chunk.TargetName is { } target ? track.Node.ChildList.FirstOrDefault(c => c.Name == target) : null;
            if (track.Target is null)
                _problems.Add($"The track '{track.Name}' (#{track.Node.Id}) has no child named after it; it still moves its {track.Node.ChildList.Count} children.");
        }
        var world = WorldMatrices(t => t.Node.Local);
        foreach (var node in _nodes) node.WorldMatrix = world[node.Index];
        // animated: below a track; set from the roots down (an explicit stack: chains may be deep)
        var stack = new Stack<XffSceneNode>(Roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            bool below = node.AsTrack is not null || node.IsAnimated;
            foreach (var child in node.ChildList)
            {
                child.IsAnimated = below;
                stack.Push(child);
            }
        }
    }

    /// <summary>
    /// Which parent links to cut so the tree has no cycle, found in one pass: the links are made in file order and the
    /// one that would close a cycle is cut, which is the link of the cycle's node that comes last in the file. A node
    /// has at most one parent, so the cycles are disjoint and each loses exactly one link.
    /// </summary>
    private static bool[] CycleCuts(XffSceneNode?[] parents)
    {
        const int Unvisited = 0, OnPath = 1, Done = 2;
        var state = new byte[parents.Length];
        var cut = new bool[parents.Length];
        var path = new List<int>();
        var place = new int[parents.Length];
        for (int start = 0; start < parents.Length; start++)
        {
            path.Clear();
            int n = start;
            while (n >= 0 && state[n] == Unvisited)
            {
                state[n] = OnPath;
                place[n] = path.Count;
                path.Add(n);
                n = parents[n]?.Index ?? -1;
            }
            if (n >= 0 && state[n] == OnPath)
            {
                // path[place[n]..] is a cycle: cut the link of its last node in file order
                int last = n;
                for (int i = place[n]; i < path.Count; i++) last = Math.Max(last, path[i]);
                cut[last] = true;
            }
            foreach (int p in path) state[p] = Done;
        }
        return cut;
    }

    private T? Resolve<T>(XffChunk from, string field, uint id, bool optional) where T : XffChunk
    {
        if (id == XffFile.NoReference)
        {
            if (!optional) Reference(from, field, id, $"is \"none\", but a {XffChunkTypes.Describe(ExpectedType<T>())} is needed");
            return null;
        }
        switch (File.Find(id))
        {
            case T found:
                return found;
            case null:
                Reference(from, field, id, "no chunk has this id");
                return null;
            case XffRawChunk raw when raw.Problem is not null:
                Reference(from, field, id, $"is a {XffChunkTypes.Describe(raw.Type)} that could not be read ({raw.Problem.TrimEnd('.')})");
                return null;
            case var other:
                Reference(from, field, id, Describe(other) + $", not a {XffChunkTypes.Describe(ExpectedType<T>())}");
                return null;
        }
    }

    private static ushort ExpectedType<T>() => typeof(T).Name switch
    {
        nameof(XffVertexBuffer) => XffChunkTypes.VertexBuffer,
        nameof(XffIndexList) => XffChunkTypes.IndexList,
        nameof(XffFaceAttributes) => XffChunkTypes.FaceAttributes,
        nameof(XffMaterial) => XffChunkTypes.Material,
        nameof(XffTexture) => XffChunkTypes.Texture,
        _ => 0,
    };

    private void BuildMeshes()
    {
        foreach (var instance in File.All<XffMeshInstance>())
        {
            if (_nodesById.GetValueOrDefault(instance.Node) is not { } node)
            {
                Reference(instance, "node", instance.Node, File.Find(instance.Node) is { } other ? Describe(other) + ", not a node" : "no chunk has this id");
                _problems.Add($"The mesh '{instance.Name}' (#{instance.Id}) belongs to no node and is left out.");
                continue;
            }
            var submeshes = new List<XffSceneSubmesh>();
            for (int i = 0; i < instance.Submeshes.Count; i++)
                if (BuildSubmesh(instance, i) is { } submesh) submeshes.Add(submesh);
            var mesh = new XffSceneMesh(instance, node, submeshes);
            node.MeshList.Add(mesh);
            _meshes.Add(mesh);
        }
    }

    private XffSceneSubmesh? BuildSubmesh(XffMeshInstance instance, int index)
    {
        var record = instance.Submeshes[index];
        string field = $"submesh {index} ";
        var vertices = Resolve<XffVertexBuffer>(instance, field + "vertex buffer", record.VertexBuffer, optional: false);
        var list = Resolve<XffIndexList>(instance, field + "index list", record.IndexList, optional: false);
        var attributes = Resolve<XffFaceAttributes>(instance, field + "face attributes", record.FaceAttributes, optional: true);
        var material = Resolve<XffMaterial>(instance, field + "material", record.Material, optional: true);
        var texture = Resolve<XffTexture>(instance, field + "texture", record.Texture, optional: true);
        var reflection = Resolve<XffTexture>(instance, field + "reflection texture", record.ReflectionTexture, optional: true);
        string where = $"Submesh {index} of '{instance.Name}' (#{instance.Id})";
        if (vertices is null || list is null)
        {
            _problems.Add($"{where} is left out: its vertices or indices do not resolve.");
            return null;
        }
        if (record.PrimitiveType != XffSubmesh.TriangleList)
        {
            _problems.Add($"{where} is left out: its primitive type {record.PrimitiveType} is not a triangle list (1).");
            return null;
        }
        long first = record.FirstIndex, count = record.IndexCount;
        if (first + count > list.Indices.Count || count % 3 != 0)
        {
            _problems.Add($"{where} is left out: it uses indices {first} to {first + count} of an index list of {list.Indices.Count}, which is not a whole range of triangles.");
            return null;
        }
        var indices = new int[count];
        for (int i = 0; i < count; i++)
        {
            int v = list.Indices[(int)first + i];
            if (v >= vertices.Count)
            {
                _problems.Add($"{where} is left out: index {v} is past its {vertices.Count} vertices.");
                return null;
            }
            indices[i] = v;
        }
        IReadOnlyList<XffColour> colours = [];
        IReadOnlyList<Vector2> uvs = [];
        if (attributes is not null)
        {
            // corner k of the submesh is corner (first + k) of the index list, and of its face attributes
            if (attributes.TriangleCount * 3L != list.Indices.Count)
                _problems.Add($"{where}: its face attributes hold {attributes.TriangleCount} triangles but its index list {list.Indices.Count / 3}; the colours and UVs are left out.");
            else
            {
                if (attributes.Colours.Count > 0) colours = Slice(attributes.Colours, (int)first, (int)count);
                if (attributes.Uvs.Count > 0) uvs = Slice(attributes.Uvs, (int)first, (int)count);
            }
        }
        return new XffSceneSubmesh(index, record, vertices, indices, colours, uvs, material, texture, reflection);
    }

    private static T[] Slice<T>(IReadOnlyList<T> list, int first, int count)
    {
        var result = new T[count];
        for (int i = 0; i < count; i++) result[i] = list[first + i];
        return result;
    }

    private void AttachProperties()
    {
        foreach (var property in File.All<XffProperty>())
        {
            if (_nodesById.GetValueOrDefault(property.Node) is { } node) node.PropertyList.Add(property);
            else Reference(property, "node", property.Node, File.Find(property.Node) is { } other ? Describe(other) + ", not a node" : "no chunk has this id");
        }
    }

    private void CheckOtherReferences()
    {
        foreach (var chunk in File.Chunks)
        {
            switch (chunk)
            {
                case XffCollision { UnknownForm: XffCollision.NodeForm } collision:
                    if (_nodesById.GetValueOrDefault(collision.Source) is null)
                        Reference(collision, "source node", collision.Source, File.Find(collision.Source) is { } other ? Describe(other) + ", not a node" : "no chunk has this id");
                    break;
                case XffCamera camera when camera.Target != XffFile.NoReference && _nodesById.GetValueOrDefault(camera.Target) is null:
                    Reference(camera, "target", camera.Target, File.Find(camera.Target) is { } target ? Describe(target) + ", not a node" : "no chunk has this id");
                    break;
                case XffSprite sprite when sprite.UnknownReference != XffFile.NoReference && File.Find(sprite.UnknownReference) is null:
                    Reference(sprite, "reference", sprite.UnknownReference, "no chunk has this id");
                    break;
            }
        }
        foreach (var problem in _references) _problems.Add("Reference: " + problem);
    }
}
