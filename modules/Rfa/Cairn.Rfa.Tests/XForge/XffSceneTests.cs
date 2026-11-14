using System.Numerics;
using Cairn.Formats.XForge;

namespace Cairn.Rfa.Tests.XForge;

/// <summary>
/// The resolved X-Forge scene (<see cref="XffScene"/>): the node tree and its world transforms, meshes with their
/// submeshes, tracks, properties, prefabs and markers, bad references, and the classification (<see cref="XffClassifier"/>).
/// </summary>
public class XffSceneTests
{
    private static readonly Quaternion QuarterTurnY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);

    private static XffScene Build(SyntheticXff x) => XffScene.Read(x.ToArray(), "test.xff");

    private static void Near(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");
    }

    private static Vector3 Origin(Matrix4x4 world) => Vector3.Transform(Vector3.Zero, world);

    private static readonly Vector3[] Quad = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)];
    private static readonly Vector3[] QuadNormals = [-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ];
    private static readonly ushort[] QuadIndices = [0, 1, 2, 2, 1, 3];

    [Fact]
    public void WorldTransforms_ComposeAKnownHierarchy()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        // a group one unit along X, turned a quarter about Y (so its +Z points along +X) and scaled by 2
        uint group = x.Group(root, "gr_a", new XffTransform(new Vector3(1, 0, 0), QuarterTurnY, new Vector3(2, 2, 2)));
        uint child = x.Object(group, "a", new XffTransform(new Vector3(0, 0, 1), Quaternion.Identity, Vector3.One));
        uint helper = x.Helper(child, "Dummy01", new Vector3(0, 1, 0));
        var scene = Build(x);

        Assert.Empty(scene.Problems);
        var r = Assert.Single(scene.Roots);
        Assert.Equal(root, r.Id);
        var g = scene.NodeById(group)!;
        var c = scene.NodeById(child)!;
        var h = scene.NodeById(helper)!;
        Assert.Same(g, c.Parent);
        Assert.Same(c, Assert.Single(g.Children));
        Near(new Vector3(1, 0, 0), Origin(g.WorldMatrix));
        // (0, 0, 1) scaled by 2 is (0, 0, 2); a quarter turn about +Y takes +Z to +X: (2, 0, 0); plus the group's (1, 0, 0)
        Near(new Vector3(3, 0, 0), Origin(c.WorldMatrix));
        // the helper is one unit up in the child, which carries the group's scale: 2 up
        Near(new Vector3(3, 2, 0), Origin(h.WorldMatrix));
        Near(new Vector3(3, 0, 0) + new Vector3(0, 0, -2), Vector3.Transform(new Vector3(1, 0, 0), c.WorldMatrix)); // +X turns to −Z
    }

    [Fact]
    public void Meshes_ResolveSubmeshes()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        XffColour[] colours = [.. Enumerable.Range(0, 6).Select(i => new XffColour((byte)(10 * i), 0, 0, 0))];
        Vector2[] uvs = [.. Enumerable.Range(0, 6).Select(i => new Vector2(i, -i))];
        uint node = x.MeshObject(root, "crate", Quad, QuadNormals, QuadIndices, colours, uvs, "lvl0_crate_01.pcx");
        uint bare = x.MeshObject(root, "glass", Quad, QuadNormals, QuadIndices, colours, null, null);
        var scene = Build(x);

        Assert.Empty(scene.Problems);
        Assert.Equal(2, scene.Meshes.Count);
        var mesh = Assert.Single(scene.NodeById(node)!.Meshes);
        Assert.Equal("mesh_crate", mesh.Name);
        Assert.Same(scene.NodeById(node), mesh.Node);
        var s = Assert.Single(mesh.Submeshes);
        Assert.Equal(Quad, s.Positions);
        Assert.Equal(QuadNormals, s.Normals);
        Assert.Equal(QuadIndices.Select(i => (int)i), s.Indices);
        Assert.Equal(2, s.TriangleCount);
        Assert.Equal(colours, s.CornerColours);
        Assert.Equal(uvs, s.CornerUvs);
        Assert.Equal("lvl0_crate_01.pcx", s.TextureName);
        Assert.NotNull(s.Material);
        var g = Assert.Single(scene.NodeById(bare)!.Meshes[0].Submeshes);
        Assert.Null(g.Texture);
        Assert.Empty(g.CornerUvs);
        Assert.Equal(6, g.CornerColours.Count);
        Assert.Equal(2, scene.Materials.Count);
        Assert.Single(scene.Textures);
    }

    [Fact]
    public void Submeshes_ShareAVertexBufferAndUseTheirIndexRange()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint node = x.Object(root, "two");
        uint material = x.Material();
        uint vb = x.VertexBuffer(Quad, QuadNormals);
        uint first = x.IndexList([0, 1, 2]);
        uint second = x.IndexList([2, 1, 3, 9, 9, 9]);
        x.MeshInstance(node, "mesh_two",
        [
            SyntheticXff.Submesh(vb, first, 3, XffFile.NoReference, material, XffFile.NoReference),
            new XffSubmesh(vb, second, XffFile.NoReference, material, XffFile.NoReference, XffFile.NoReference, 0, 3, 1), // the first triangle of six indices
        ]);
        var scene = Build(x);
        Assert.Empty(scene.Problems);
        var subs = scene.Meshes[0].Submeshes;
        Assert.Equal(2, subs.Count);
        Assert.Same(subs[0].Vertices, subs[1].Vertices);
        Assert.Equal(new[] { 0, 1, 2 }, subs[0].Indices);
        Assert.Equal(new[] { 2, 1, 3 }, subs[1].Indices);
    }

    [Fact]
    public void BadReferences_AreReportedAndLeftOut()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint orphan = x.Object(500, "orphan"); // a parent that does not exist
        uint texture = x.Texture("t.tga");
        uint node = x.Object(root, "box");
        uint material = x.Material();
        uint vb = x.VertexBuffer(Quad, QuadNormals);
        uint il = x.IndexList(QuadIndices);
        uint wrong = x.IndexList([0, 1, 7]); // index 7 is past the four vertices
        x.MeshInstance(node, "mesh_box",
        [
            SyntheticXff.Submesh(texture, il, 6, XffFile.NoReference, material, XffFile.NoReference), // a texture as the vertex buffer
            SyntheticXff.Submesh(vb, il, 6, XffFile.NoReference, 900, XffFile.NoReference), // a material that does not exist
            SyntheticXff.Submesh(vb, wrong, 3, XffFile.NoReference, material, XffFile.NoReference),
            SyntheticXff.Submesh(vb, il, 9, XffFile.NoReference, material, XffFile.NoReference), // more indices than the list holds
        ]);
        x.MeshInstance(777, "mesh_ghost", []);
        x.Property(778, "room");
        var scene = Build(x);

        var o = scene.NodeById(orphan)!;
        Assert.Null(o.Parent);
        Assert.Contains(o, scene.Roots);
        Assert.Contains(scene.References, r => r.FromId == orphan && r.Field == "parent" && r.TargetId == 500 && r.Problem == "no chunk has this id");
        Assert.Contains(scene.References, r => r.Field == "submesh 0 vertex buffer" && r.TargetId == texture && r.Problem.Contains("is a texture, not a vertex buffer"));
        Assert.Contains(scene.References, r => r.Field == "submesh 1 material" && r.TargetId == 900);
        Assert.Contains(scene.References, r => r.Field == "node" && r.TargetId == 777);
        Assert.Contains(scene.References, r => r.FromType == XffChunkTypes.Property && r.TargetId == 778);
        var mesh = Assert.Single(scene.NodeById(node)!.Meshes);
        var kept = Assert.Single(mesh.Submeshes); // submesh 1 resolves without its material
        Assert.Equal(1, kept.Index);
        Assert.Null(kept.Material);
        Assert.Contains(scene.Problems, p => p.Contains("Submesh 2") && p.Contains("index 7"));
        Assert.Contains(scene.Problems, p => p.Contains("Submesh 3") && p.Contains("not a whole range"));
        Assert.Contains(scene.Problems, p => p.Contains("mesh_ghost"));
        Assert.DoesNotContain(scene.Meshes, m => m.Name == "mesh_ghost");
    }

    [Fact]
    public void FaceAttributesOfAnotherSize_AreLeftOut()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint node = x.Object(root, "a");
        uint vb = x.VertexBuffer(Quad, QuadNormals);
        uint il = x.IndexList(QuadIndices);
        uint fa = x.FaceAttributes(1, null, [new(0, 0), new(1, 0), new(0, 1)]);
        x.MeshInstance(node, "mesh_a", [SyntheticXff.Submesh(vb, il, 6, fa, x.Material(), XffFile.NoReference)]);
        var scene = Build(x);
        var s = Assert.Single(scene.Meshes[0].Submeshes);
        Assert.Empty(s.CornerUvs);
        Assert.Contains(scene.Problems, p => p.Contains("face attributes hold 1 triangles"));
    }

    [Fact]
    public void ParentCycles_AreCut()
    {
        var x = new SyntheticXff();
        x.Root();
        uint a = x.NextId;
        x.Group(a + 1, "a");
        x.Group(a, "b");
        var scene = Build(x);
        Assert.Contains(scene.References, r => r.Problem.Contains("its own ancestor"));
        Assert.Equal(2, scene.Roots.Count);
        Assert.All(scene.Nodes, n => Assert.True(float.IsFinite(n.WorldMatrix.M41)));
    }

    /// <summary>A long cycle of forward parent links loses exactly one link: the one that would close it, of its last node in the file.</summary>
    [Fact]
    public void LongParentCycle_IsCutOnceAtItsLastNode()
    {
        const int count = 5_000;
        var x = new SyntheticXff();
        x.Root();
        uint first = x.NextId;
        for (int i = 0; i < count; i++) x.Group(first + (uint)((i + 1) % count), "g" + i);
        var scene = Build(x);
        var cut = Assert.Single(scene.References);
        Assert.Equal(first + count - 1, cut.FromId);
        Assert.Contains("its own ancestor", cut.Problem);
        Assert.Equal(2, scene.Roots.Count);
        Assert.Equal(count - 1, scene.Nodes.Count(n => n.Parent is not null));
    }

    /// <summary>
    /// Depth costs nothing extra: a part 20,000 nodes below its track builds (no recursion, cycles found in one pass) in
    /// well under a second and is animated, where walking to the root for every node took over five seconds.
    /// </summary>
    [Fact]
    public void DeepChain_BuildsInLinearTime()
    {
        const int depth = 20_000;
        var x = new SyntheticXff();
        uint root = x.Root();
        uint marker = x.Marker(3202, root, "player");
        uint parent = x.Track(marker, "anim_body", 0, 66, 30f, [new(Vector3.Zero, Quaternion.Identity), new(Vector3.UnitX, Quaternion.Identity)]);
        for (int i = 0; i < depth; i++) parent = x.Group(parent, "g" + i);
        uint body = x.MeshObject(parent, "body", Quad, QuadNormals, QuadIndices, null, null, null);
        byte[] bytes = x.ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var kind = XffClassifier.TryClassify(bytes, "enemy_deep.xff");
        var scene = XffScene.Read(bytes, "enemy_deep.xff");
        watch.Stop();
        Assert.Equal(XffKind.Character, kind);
        Assert.True(scene.NodeById(body)!.IsAnimated);
        Assert.False(scene.NodeById(marker)!.IsAnimated);
        Assert.Empty(scene.References);
        // generous for a loaded machine; the quadratic walk took over five seconds here
        Assert.True(watch.ElapsedMilliseconds < 2_000, $"classifying and building took {watch.ElapsedMilliseconds:N0} ms");
    }

    [Fact]
    public void Tracks_AnimateTheirChildren()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint marker = x.Marker(3202, root, "player", new XffTransform(new Vector3(10, 0, 0), Quaternion.Identity, Vector3.One));
        uint track = x.Track(marker, "anim_body", 0, 100, 10f, [new(new(0, 1, 0), Quaternion.Identity), new(new(0, 3, 0), Quaternion.Identity)]);
        uint body = x.MeshObject(track, "body", Quad, QuadNormals, QuadIndices, null, null, null); // forward of nothing, child of the track
        uint arm = x.Object(body, "arm", new XffTransform(new Vector3(1, 0, 0), Quaternion.Identity, Vector3.One));
        uint loose = x.Track(root, "anim_nobody", -50, 50, 10f, [new(Vector3.Zero, Quaternion.Identity)]);
        var scene = Build(x);

        Assert.Equal(2, scene.Tracks.Count);
        var t = scene.NodeById(track)!.AsTrack!;
        Assert.Same(scene.NodeById(body), t.Target);
        Assert.Same(t, scene.NodeById(body)!.Track);
        Assert.Null(scene.NodeById(arm)!.Track);
        Assert.True(scene.NodeById(body)!.IsAnimated);
        Assert.True(scene.NodeById(arm)!.IsAnimated);
        Assert.False(scene.NodeById(marker)!.IsAnimated);
        Assert.Equal((0, 100, 10f, 100, true), (t.Start, t.End, t.Rate, t.Interval, t.Loops));
        Assert.Equal(100, t.KeyTime(1));
        // at rest a track is at its first key
        Near(new Vector3(10, 1, 0), Origin(scene.NodeById(body)!.WorldMatrix));
        Near(new Vector3(11, 1, 0), Origin(scene.NodeById(arm)!.WorldMatrix));
        // half way the position is between the keys, and the track loops over its range
        Near(new Vector3(0, 2, 0), t.Sample(50).Position);
        Near(new Vector3(0, 2, 0), t.Sample(150).Position);
        Near(new Vector3(0, 2, 0), t.Sample(-50).Position);
        var world = scene.WorldMatricesAt(50);
        Near(new Vector3(11, 2, 0), Origin(world[scene.NodeById(arm)!.Index]));
        Assert.Null(scene.NodeById(loose)!.AsTrack!.Target);
        Assert.Contains(scene.Problems, p => p.Contains("anim_nobody"));
        Assert.Equal(XffKind.Character, scene.Kind);
    }

    [Fact]
    public void PropertiesPrefabsAndMarkers()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint area = x.Marker(XffMarkers.Area, root, "room01");
        uint col = x.CollisionBox(area, "col_room01", new(-1, -1, -1), new(1, 1, 1));
        x.Property(col, "room");
        x.Property(col, "m:1");
        uint spawn = x.Marker(4202, root, "enemy01");
        x.Marker(4202, root, "enemy02");
        uint prefab = x.Prefab(area, "health", XffTransform.Identity, "item_healthpack.xff");
        var scene = Build(x);

        Assert.Empty(scene.Problems);
        var c = scene.NodeById(col)!;
        Assert.Equal(new[] { "room", "m:1" }, c.Properties.Select(p => p.Text));
        Assert.True(c.HasProperty("room"));
        Assert.Same(c, Assert.Single(scene.NodesWithProperty("room")));
        Assert.Equal("item_healthpack.xff", Assert.Single(scene.Prefabs).Prefab!.FileName);
        Assert.Equal(prefab, scene.Prefabs[0].Id);
        Assert.Equal(3, scene.Markers.Count);
        Assert.Equal(2, scene.MarkersOfType(4202).Count);
        Assert.Equal(new ushort[] { XffMarkers.Area, 4202 }, scene.MarkersByType.Select(g => g.Key));
        Assert.Equal(spawn, scene.MarkersOfType(4202)[0].Id);
        Assert.Equal((ushort)3202, XffMarkers.SpawnedEnemyType(4202));
        Assert.Null(XffMarkers.SpawnedEnemyType(3202));
        Assert.Equal(XffKind.Level, scene.Kind);
    }

    [Fact]
    public void Tracks_SampleAsTheGameDoes()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        XffKey[] keys = [new(new(0, 0, 0), Quaternion.Identity), new(new(0, 33, 0), Quaternion.Identity), new(new(0, 66, 0), Quaternion.Identity)];
        // 30 keys per second: a key every 33 ms from 0, whatever the start; times before 0 hold key 0
        uint offset = x.Track(root, "anim_a", -33, 33, 30f, keys);
        // without the loop flag the time is clamped to the range
        uint clamped = x.Track(root, "anim_b", 0, 66, 30f, keys, XffAnimationFlags.Position | XffAnimationFlags.Rotation);
        var scene = Build(x);
        var a = scene.NodeById(offset)!.AsTrack!;
        Assert.Equal(33, a.Interval);
        Assert.Equal(66, a.KeyTime(2));
        Near(Vector3.Zero, a.Sample(-10).Position);
        Near(new Vector3(0, 16.5f, 0), a.Sample(16.5).Position);
        Near(new Vector3(0, 16.5f, 0), a.Sample(16.5 + 66).Position); // wraps over [−33, 33]
        var b = scene.NodeById(clamped)!.AsTrack!;
        Assert.False(b.Loops);
        Near(new Vector3(0, 66, 0), b.Sample(500).Position);
        Near(Vector3.Zero, b.Sample(-500).Position);
    }

    // ── classification ─────────────────────────────────────────────────────

    [Fact]
    public void Classify_LevelByMarkerOrRooms()
    {
        var byMarker = new SyntheticXff();
        byMarker.Marker(XffMarkers.LevelEnd, byMarker.Root(), "gr_levelend_col");
        Assert.Equal(XffKind.Level, Build(byMarker).Kind);

        var byRooms = new SyntheticXff();
        uint room = byRooms.Object(byRooms.Root(), "room01");
        byRooms.Property(room, "room");
        Assert.Equal(XffKind.Level, Build(byRooms).Kind);

        // a button with a stray room property is still a button (its own marker says what it is)
        var button = new SyntheticXff();
        uint gr = button.Marker(XffMarkers.Button, button.Root(), "gr_door01");
        button.Property(button.Object(gr, "door01"), "room");
        var (kind, reason) = XffClassifier.Explain(Build(button));
        Assert.Equal(XffKind.Model, kind);
        Assert.Contains("room property", reason);
    }

    [Fact]
    public void Classify_CharacterNeedsAnimatedMeshesAndAnActorMarker()
    {
        static SyntheticXff Actor(ushort marker, bool animated)
        {
            var x = new SyntheticXff();
            uint m = x.Marker(marker, x.Root(), "player");
            uint parent = animated ? x.Track(m, "anim_body", 0, 533, 30, [new(Vector3.Zero, Quaternion.Identity)]) : m;
            x.MeshObject(parent, "body", Quad, QuadNormals, QuadIndices, null, null, null);
            return x;
        }
        Assert.Equal(XffKind.Character, Build(Actor(3202, true)).Kind);
        Assert.Equal(XffKind.Character, Build(Actor(XffMarkers.PlayerClient, true)).Kind);
        Assert.Equal(XffKind.Character, Build(Actor(XffMarkers.Player, true)).Kind);
        Assert.Equal(XffKind.Model, Build(Actor(3202, false)).Kind);
        Assert.Equal(XffKind.Model, Build(Actor(XffMarkers.Door, true)).Kind); // an animated door is a model
        var item = new SyntheticXff();
        item.MeshObject(item.Marker(2042, item.Root(), "gr_item_healthpack_mesh"), "mesh", Quad, QuadNormals, QuadIndices, null, null, "a.tga");
        Assert.Equal(XffKind.Model, Build(item).Kind);
    }

    [Fact]
    public void TryClassify_RefusesWhatIsNotAScene()
    {
        var level = new SyntheticXff();
        level.Marker(XffMarkers.Area, level.Root(), "room01");
        Assert.Equal(XffKind.Level, XffClassifier.TryClassify(level.ToArray(), "a.xff"));
        Assert.Null(XffClassifier.TryClassify("not a scene file at all"u8, "a.xff"));
        var cut = level.ToArray();
        Assert.Null(XffClassifier.TryClassify(cut.AsSpan(0, cut.Length - 3), "a.xff"));
    }

    /// <summary>The content claim's promise: a damaged scene (bytes overwritten at random, a fixed seed) never makes <see cref="XffClassifier.TryClassify"/> throw.</summary>
    [Fact]
    public void TryClassify_NeverThrowsForDamagedScenes()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint marker = x.Marker(3202, root, "player");
        uint track = x.Track(marker, "anim_body", 0, 66, 30f, [new(Vector3.Zero, Quaternion.Identity), new(Vector3.UnitX, QuarterTurnY)]);
        x.MeshObject(track, "body", Quad, QuadNormals, QuadIndices, null, [new(0, 0), new(1, 0), new(0, 1), new(0, 1), new(1, 0), new(1, 1)], "skin.tga");
        x.Property(root, "room");
        byte[] good = x.ToArray();
        var random = new Random(1234);
        for (int round = 0; round < 3_000; round++)
        {
            byte[] bad = (byte[])good.Clone();
            for (int k = random.Next(1, 6); k > 0; k--)
                bad[random.Next(XffChunkHeader.Size + 4, bad.Length)] = random.Next(3) switch { 0 => 0xFF, 1 => 0, _ => (byte)random.Next(256) };
            var thrown = Record.Exception(() => XffClassifier.TryClassify(bad, "damaged.xff"));
            Assert.True(thrown is null, $"round {round}: {thrown?.GetType().Name}: {thrown?.Message}");
        }
    }

    [Fact]
    public void Space_ConvertsToRedFaction()
    {
        Assert.Equal(new Vector3(2.35f, 0, -4.7f), XffSpace.ToRfPosition(new Vector3(1, 0, -2)));
        Assert.Equal(-Vector3.UnitY, XffSpace.ToRfNormal(Vector3.UnitY));
        Assert.Equal((1, 3, 2), XffSpace.ToRfTriangle(1, 2, 3));
        Assert.Equal(Matrix4x4.Identity, XffSpace.LocalMatrix(XffTransform.Identity));
        // a zero quaternion (never in the data) is taken as no rotation rather than collapsing the node
        Assert.Equal(Quaternion.Identity, XffSpace.SafeRotation(default));
    }
}
