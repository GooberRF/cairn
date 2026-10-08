using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.V3d;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// The Red Faction PlayStation 2 mesh readers (<see cref="RfmFormat"/>, <see cref="RfcFormat"/>): synthetic
/// files built here (no game bytes), malformed and truncated input, and — when the PS2 files are configured
/// in research/local-paths.json — every real file, their exporter twins (.v3d / .vcm) and the compiled
/// .v3m twins on the demo disc.
/// </summary>
public class Ps2MeshTests(ITestOutputHelper output)
{
    // ── Synthetic files ─────────────────────────────────────────────────────

    /// <summary>A face as the synthetic writer stores it.</summary>
    private sealed record SynthFace(int A, int B, int C, int Texture, Vector3 Normal, Vector2 Uv0, Vector2 Uv1, Vector2 Uv2, int Chrome = 0, int Self = 0);

    private sealed class SynthChunk
    {
        public List<Vector3> Positions { get; } = [];
        public List<Vector3> Normals { get; } = [];
        public List<SynthFace> Faces { get; } = [];
        public List<(byte[] Weights, byte[] Bones)> Weights { get; } = [];
        public byte[]? Map { get; set; }
    }

    private sealed class SynthSubmesh
    {
        public int Flags { get; set; }
        public int SourceVertices { get; set; }
        public Vector3 Centre { get; set; }
        public List<SynthChunk> Chunks { get; } = [];
        public List<string> Textures { get; } = [];
        public List<(string Name, uint Flags)>? Materials { get; set; } = [];
    }

    private sealed class SynthMesh
    {
        public bool Character { get; set; }
        public int SubmeshCount { get; set; } = 1;
        public List<SynthSubmesh> Submeshes { get; } = [];
        public List<(string Name, Quaternion Rotation, Vector3 Position, int Parent)> Bones { get; } = [];
        public List<(string Name, int Bone, Vector3 Position, float Radius)> Spheres { get; } = [];
        public List<(string Name, int Parent, Quaternion Rotation, Vector3 Position)> Props { get; } = [];

        public byte[] Build(int kindField = -1, int version = 1)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(0x87128712u);
            w.Write(kindField >= 0 ? kindField : Character ? 1 : 0);
            w.Write(version);
            w.Write(SubmeshCount);
            w.Write(1);
            w.Write(Spheres.Count);
            w.Write(Props.Count);
            w.Write(Submeshes.Sum(s => s.Materials?.Count ?? 0));
            if (Bones.Count > 0)
            {
                Section(w, 0x424F4E45, b =>
                {
                    b.Write(Bones.Count);
                    foreach (var (name, rot, pos, parent) in Bones)
                    {
                        Name(b, name, 24);
                        b.Write(rot.X); b.Write(rot.Y); b.Write(rot.Z); b.Write(rot.W);
                        b.Write(pos.X); b.Write(pos.Y); b.Write(pos.Z);
                        b.Write(parent);
                    }
                });
            }
            foreach (var (name, bone, pos, radius) in Spheres)
            {
                Section(w, 0x43535048, b =>
                {
                    Name(b, name, 24);
                    b.Write(bone);
                    b.Write(pos.X); b.Write(pos.Y); b.Write(pos.Z);
                    b.Write(radius);
                });
            }
            foreach (var (name, parent, rot, pos) in Props)
            {
                Section(w, 0x44554D42, b =>
                {
                    Name(b, name, 24);
                    b.Write(parent);
                    b.Write(rot.X); b.Write(rot.Y); b.Write(rot.Z); b.Write(rot.W);
                    b.Write(pos.X); b.Write(pos.Y); b.Write(pos.Z);
                });
            }
            foreach (var s in Submeshes)
            {
                Section(w, 0x87251110, b => WriteSubmesh(b, s));
                if (s.Materials is { } mats)
                {
                    Section(w, 0x11133344, b =>
                    {
                        b.Write(mats.Count);
                        foreach (var (name, flags) in mats)
                        {
                            Name(b, name, 32);
                            b.Write(0f); b.Write(0f); b.Write(0f); b.Write(0f);
                            Name(b, "", 32);
                            b.Write(flags);
                        }
                    });
                }
            }
            w.Write(0);
            w.Write(0);
            return ms.ToArray();
        }

        private void WriteSubmesh(BinaryWriter b, SynthSubmesh s)
        {
            b.Write(float.MaxValue);
            b.Write(5);
            b.Write(s.Flags);
            b.Write(s.SourceVertices);
            var all = s.Chunks.SelectMany(c => c.Positions).ToList();
            var max = all.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            var min = all.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            foreach (var v in new[] { max, min, s.Centre }) { b.Write(v.X); b.Write(v.Y); b.Write(v.Z); }
            b.Write(1f);
            using var block = new MemoryStream();
            using var d = new BinaryWriter(block);
            var table = new List<(int, int, int, int, int)>();
            foreach (var c in s.Chunks)
            {
                int nv = c.Positions.Count, nf = c.Faces.Count;
                int vb = Align(nv * 12), fb = Align(nf * 80), wb = Align(nv * 8);
                foreach (var p in c.Positions) { d.Write(p.X); d.Write(p.Y); d.Write(p.Z); }
                Pad(d);
                foreach (var n in c.Normals) { d.Write(n.X); d.Write(n.Y); d.Write(n.Z); }
                Pad(d);
                foreach (var f in c.Faces)
                {
                    d.Write(f.A); d.Write(f.B); d.Write(f.C); d.Write(f.Texture);
                    d.Write(f.Normal.X); d.Write(f.Normal.Y); d.Write(f.Normal.Z); d.Write(0f);
                    d.Write(f.Uv0.X); d.Write(f.Uv0.Y); d.Write(0f); d.Write(0f);
                    d.Write(f.Uv1.X); d.Write(f.Uv1.Y); d.Write(0f); d.Write(0f);
                    d.Write(f.Uv2.X); d.Write(f.Uv2.Y); d.Write(f.Chrome); d.Write(f.Self);
                }
                Pad(d);
                for (int i = 0; i < nv; i++)
                {
                    if (i < c.Weights.Count) { d.Write(c.Weights[i].Weights); d.Write(c.Weights[i].Bones); }
                    else d.Write(new byte[8]);
                }
                Pad(d);
                if (Character)
                {
                    d.Write(c.Map ?? Enumerable.Repeat((byte)0xFF, s.SourceVertices).ToArray());
                    Pad(d);
                }
                table.Add((nv, nf, vb, fb, wb));
            }
            d.Flush();
            b.Write((int)block.Length);
            b.Write(block.ToArray());
            b.Write((ushort)table.Count);
            foreach (var (nv, nf, vb, fb, wb) in table)
            {
                b.Write((ushort)nv); b.Write((ushort)nf); b.Write((ushort)vb); b.Write((ushort)fb); b.Write((ushort)wb);
            }
            b.Write(s.Textures.Count);
            foreach (var t in s.Textures) { b.Write(Encoding.Latin1.GetBytes(t)); b.Write((byte)0); }
        }

        private static int Align(int n) => (n + 15) & ~15;

        private static void Pad(BinaryWriter d)
        {
            d.Flush();
            while (d.BaseStream.Length % 16 != 0) d.Write((byte)0);
        }

        private static void Name(BinaryWriter b, string text, int size)
        {
            var bytes = new byte[size];
            Encoding.Latin1.GetBytes(text, bytes);
            b.Write(bytes);
        }

        private static void Section(BinaryWriter w, uint type, Action<BinaryWriter> body)
        {
            using var ms = new MemoryStream();
            using var b = new BinaryWriter(ms);
            body(b);
            b.Flush();
            w.Write(type);
            w.Write((int)ms.Length);
            w.Write(ms.ToArray());
        }
    }

    private static readonly Vector3 Up = Vector3.UnitZ;

    /// <summary>
    /// A unit square in two chunks: chunk 0 holds triangle (0,1,2), chunk 1 holds copies of corners 0 and
    /// 2 plus corner 3 and the second triangle stored with its winding reversed (as the PS2 tool often
    /// does), and a third triangle with the second texture.
    /// </summary>
    private static SynthMesh Square(bool character)
    {
        var p = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
        var uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        var sub = new SynthSubmesh { Flags = character ? 1 : 0, SourceVertices = 4, Centre = new Vector3(0.5f, 0.5f, 0) };
        sub.Textures.AddRange(["square.tga", "Second.TGA"]);
        sub.Materials = [("square.tga", 0), ("second.tga", 1)];

        var c0 = new SynthChunk();
        c0.Positions.AddRange([p[0], p[1], p[2]]);
        c0.Normals.AddRange([Up, Up, Up]);
        c0.Faces.Add(new SynthFace(0, 1, 2, 0, Up, uv[0], uv[1], uv[2]));
        var c1 = new SynthChunk();
        c1.Positions.AddRange([p[0], p[2], p[3]]);
        c1.Normals.AddRange([Up, Up, Up]);
        c1.Faces.Add(new SynthFace(0, 2, 1, 0, Up, uv[0], uv[3], uv[2])); // reversed: true order is 0, 2(p2), 3
        c1.Faces.Add(new SynthFace(0, 1, 2, 1, Up, uv[0] + new Vector2(0, 1), uv[2] + new Vector2(0, 1), uv[3] + new Vector2(0, 1), Chrome: 1));
        if (character)
        {
            byte[] none = [0xFF, 0xFF, 0xFF];
            c0.Weights.AddRange([([16, 0, 0, 0], [0, .. none]), ([12, 4, 0, 0], [0, 1, 0xFF, 0xFF]), ([16, 0, 0, 0], [1, .. none])]);
            c1.Weights.AddRange([([16, 0, 0, 0], [0, .. none]), ([16, 0, 0, 0], [1, .. none]), ([8, 8, 0, 0], [1, 0, 0xFF, 0xFF])]);
            c0.Map = [0, 1, 2, 0xFF];
            c1.Map = [0, 0xFF, 1, 2];
        }
        sub.Chunks.AddRange([c0, c1]);

        var mesh = new SynthMesh { Character = character };
        mesh.Submeshes.Add(sub);
        if (character)
        {
            mesh.Bones.Add(("root", Quaternion.Identity, Vector3.Zero, -1));
            mesh.Bones.Add(("child", Quaternion.Identity, new Vector3(0, -1, 0), 0));
            mesh.Spheres.Add(("head", 1, new Vector3(0, 0.5f, 0), 0.25f));
        }
        mesh.Props.Add(("muzzle", character ? 1 : -1, Quaternion.Identity, new Vector3(1, 2, 3)));
        return mesh;
    }

    private static LegacyMesh Read(byte[] bytes, string name)
    {
        ILegacyMeshFormat format = name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase) ? new RfcFormat() : new RfmFormat();
        Assert.True(format.TryRead(bytes, name, out var mesh, out var error), error?.Message);
        return mesh!;
    }

    [Fact]
    public void Formats_HaveTheirExtensionsAndRecogniseTheirKind()
    {
        var rfm = Square(false).Build();
        var rfc = Square(true).Build();
        Assert.Equal(".rfm", new RfmFormat().Extension);
        Assert.Equal(".rfc", new RfcFormat().Extension);
        Assert.True(new RfmFormat().Recognises(rfm));
        Assert.False(new RfmFormat().Recognises(rfc));
        Assert.True(new RfcFormat().Recognises(rfc));
        Assert.False(new RfcFormat().Recognises(rfm));
        Assert.False(new RfmFormat().Recognises(rfm.AsSpan(0, 8)));
        Assert.False(new RfmFormat().Recognises([.. "RF3D"u8, .. new byte[12]]));
    }

    [Fact]
    public void StaticMesh_ReJoinsChunkCopiesAndRestoresWinding()
    {
        var mesh = Read(Square(false).Build(), "folder/crate.rfm");
        var d = mesh.Description;
        Assert.Equal("rfm", mesh.SourceFormat);
        Assert.Equal(V3dKind.StaticMesh, d.Kind);
        var sub = Assert.Single(d.Submeshes);
        Assert.Equal("crate", sub.Name.Text);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0), sub.Offset);
        var lod = Assert.Single(sub.Lods);
        Assert.Equal(4, lod.VertexCountOverride);
        Assert.Equal(2, lod.Groups.Length);

        // Material 0: the two square triangles, four vertices (corners 0 and 2 were copied into both chunks).
        var g = lod.Groups[0];
        Assert.Equal(0, g.Material);
        Assert.Equal(4, g.Vertices.Length);
        Assert.Equal(2, g.Triangles.Length);
        foreach (var t in g.Triangles)
        {
            var n = Vector3.Cross(g.Vertices[t.B].Position - g.Vertices[t.A].Position, g.Vertices[t.C].Position - g.Vertices[t.A].Position);
            Assert.True(n.Z > 0, "every triangle faces +Z like its stored normal");
            Assert.False(t.IsDoubleSided);
        }
        // UVs follow their corners through the flip.
        foreach (var v in g.Vertices) Assert.Equal(new Vector2(v.Position.X, v.Position.Y), v.TexCoord);

        // The second texture is matched to its material case-insensitively; that material is two-sided.
        var second = lod.Groups[1];
        Assert.Equal(1, second.Material);
        Assert.True(second.Triangles[0].IsDoubleSided);
        Assert.Equal(new Vector2(1, 2), second.Vertices[second.Triangles[0].B].TexCoord);
        Assert.Equal(0x1u, sub.Materials[0].Flags);
        Assert.Equal(0x11u, sub.Materials[1].Flags);

        var prop = Assert.Single(d.PropPoints);
        Assert.Equal("muzzle", prop.Name.Text);
        Assert.Equal(V3dPropPoint.NameSize, prop.Name.Length);
        Assert.Equal(new Vector3(1, 2, 3), prop.Position);
        Assert.Empty(d.Bones);

        Assert.Contains(mesh.Notes, n => n.Contains("named 'crate'"));
        Assert.Contains(mesh.Notes, n => n.Contains("9 face corners in 2 chunks (6 stored vertices") && n.Contains("became 7 vertices"));
        Assert.Contains(mesh.Notes, n => n.StartsWith("1 of 3 triangles are stored with the opposite winding"));
        Assert.Contains(mesh.Notes, n => n.Contains("chrome on 1 faces"));
        Assert.Contains(mesh.Notes, n => n.Contains("1 triangles use two-sided materials"));
    }

    [Fact]
    public void Character_KeepsSkeletonWeightsAndSourceVertexOrder()
    {
        var mesh = Read(Square(true).Build(), "guard.rfc");
        var d = mesh.Description;
        Assert.Equal("rfc", mesh.SourceFormat);
        Assert.Equal(V3dKind.Character, d.Kind);
        Assert.Equal(["root", "child"], d.Bones.Select(b => b.Name.Text));
        Assert.Equal([-1, 0], d.Bones.Select(b => b.ParentIndex));
        Assert.Equal(new Vector3(0, -1, 0), d.Bones[1].Position);
        var sphere = Assert.Single(d.CollisionSpheres);
        Assert.Equal(("head", 1, 0.25f), (sphere.Name.Text, sphere.BoneIndex, sphere.Radius));
        Assert.Equal(1, d.PropPoints[0].ParentIndex);

        var lod = Assert.Single(d.Submeshes).Lods[0];
        Assert.Null(lod.VertexCountOverride);
        Assert.Equal([new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0)], lod.MorphVertices!.Value.ToArray());

        var g = lod.Groups[0];
        // Corners 0 and 2 are stored in both chunks with the same weights: each joins into one vertex.
        var corner1 = g.Vertices.Single(v => v.Position == new Vector3(1, 0, 0));
        Assert.Equal([new V3dBoneInfluence(0, 0.75f), new V3dBoneInfluence(1, 0.25f)], corner1.Influences.ToArray());
        Assert.Single(g.Vertices, v => v.Position == Vector3.Zero);
        var corner3 = g.Vertices.Single(v => v.Position == new Vector3(0, 1, 0));
        Assert.Equal([new V3dBoneInfluence(1, 0.5f), new V3dBoneInfluence(0, 0.5f)], corner3.Influences.ToArray());
        Assert.Contains(mesh.Notes, n => n.Contains("steps of 1/16"));

        // It compiles to a .v3c that Cairn reads back with the same links.
        var built = V3dBuilder.Build(d);
        var back = V3dReader.Read(V3dWriter.Write(built), "guard.v3c");
        Assert.Equal(V3dKind.Character, back.Kind);
        var batch = back.Submeshes.Single().Lods[0].Batches[0];
        int i = batch.Positions.IndexOf(new Vector3(1, 0, 0));
        Assert.Equal((191, 64, 0, 1), (batch.BoneLinks[i].Weight0, batch.BoneLinks[i].Weight1, batch.BoneLinks[i].Bone0, batch.BoneLinks[i].Bone1));
        Assert.Equal(4, back.Submeshes.Single().Lods[0].VertexCount);
    }

    [Fact]
    public void ExtraStoredSubmeshes_BecomeLevelsOfDetail()
    {
        var mesh = Square(false);
        mesh.Submeshes.Add(Square(false).Submeshes[0]);
        mesh.Submeshes.Add(Square(false).Submeshes[0]);
        mesh.SubmeshCount = 1;
        var read = Read(mesh.Build(), "light.rfm");
        var sub = Assert.Single(read.Description.Submeshes);
        Assert.Equal([0f, 10f, 20f], sub.Lods.Select(l => l.Distance));
        Assert.Contains(read.Notes, n => n.Contains("3 levels of detail") && n.Contains("10 m, 20 m"));

        // Separate submeshes when the header counts them all: made-up names with numbers.
        mesh.SubmeshCount = 3;
        read = Read(mesh.Build(), "a_very_long_debris_file_name.rfm");
        Assert.Equal(["a_very_long_debris_fi_1", "a_very_long_debris_fi_2", "a_very_long_debris_fi_3"],
            read.Description.Submeshes.Select(s => s.Name.Text));
        Assert.All(read.Description.Submeshes, s => Assert.Single(s.Lods));
    }

    [Fact]
    public void UnknownTexture_GetsAPlainMaterial()
    {
        var mesh = Square(false);
        mesh.Submeshes[0].Materials = null; // no materials section at all
        var read = Read(mesh.Build(), "bare.rfm");
        var sub = read.Description.Submeshes[0];
        Assert.Equal(["square.tga", "Second.TGA"], sub.Materials.Select(m => m.DiffuseMap.Text));
        Assert.Contains(read.Notes, n => n.Contains("no materials section"));
        Assert.Contains(read.Notes, n => n.Contains("plain material"));
    }

    [Fact]
    public void KindMismatch_IsReadAsStoredWithANote()
    {
        var read = Read(Square(true).Build(), "named_wrong.rfm");
        Assert.Equal(V3dKind.Character, read.Description.Kind);
        Assert.StartsWith("The file is a character mesh (.rfc)", read.Notes[0]);
    }

    [Fact]
    public void RedFactionII_IsRefused()
    {
        var bytes = Square(false).Build(kindField: 0x114, version: 0);
        Assert.False(new RfmFormat().Recognises(bytes));
        Assert.False(new RfcFormat().Recognises(bytes));
        Assert.False(new RfmFormat().TryRead(bytes, "rf2.rfm", out var mesh, out var error));
        Assert.Null(mesh);
        Assert.StartsWith("Red Faction II mesh (version 0x114): not supported", error!.Message);
        Assert.False(new RfcFormat().TryRead(bytes, "rf2.rfc", out _, out error));
        Assert.Contains("Red Faction II", error!.Message);

        Assert.False(new RfmFormat().TryRead(Square(false).Build(version: 2), "v2.rfm", out _, out error));
        Assert.Contains("unknown PlayStation 2 mesh header", error!.Message);
        Assert.False(new RfmFormat().TryRead("RF3D"u8.ToArray(), "x.rfm", out _, out error));
        Assert.Contains("not a Red Faction PlayStation 2 mesh", error!.Message);
    }

    [Fact]
    public void Truncation_NeverThrows()
    {
        foreach (bool character in new[] { false, true })
        {
            var bytes = Square(character).Build();
            ILegacyMeshFormat format = character ? new RfcFormat() : new RfmFormat();
            for (int length = 0; length < bytes.Length; length++)
            {
                bool ok = format.TryRead(bytes.AsSpan(0, length), "cut", out var mesh, out var error);
                Assert.True(ok ? mesh is not null : error is not null);
                Assert.False(ok, $"a file cut to {length} of {bytes.Length} bytes has no end section");
            }
            Assert.True(format.TryRead(bytes, "whole", out _, out _));
        }
    }

    [Fact]
    public void Mutations_NeverThrowNorHang()
    {
        var random = new Random(1234);
        foreach (bool character in new[] { false, true })
        {
            var original = Square(character).Build();
            ILegacyMeshFormat format = character ? new RfcFormat() : new RfmFormat();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int round = 0; round < 3000; round++)
            {
                var bytes = (byte[])original.Clone();
                int edits = 1 + random.Next(4);
                for (int e = 0; e < edits; e++)
                {
                    int at = random.Next(bytes.Length);
                    switch (random.Next(3))
                    {
                        case 0: bytes[at] = (byte)random.Next(256); break;
                        case 1: bytes[at] ^= (byte)(1 << random.Next(8)); break;
                        default:
                            // A huge count or size where an int32 is.
                            if (at + 4 <= bytes.Length) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at & ~3), random.Next(2) == 0 ? int.MaxValue : -random.Next(1, 100));
                            break;
                    }
                }
                bool ok = format.TryRead(bytes, "fuzz", out var mesh, out var error);
                Assert.True(ok ? mesh is not null && error is null : mesh is null && error is not null);
                if (ok) V3dBuilder.Build(mesh!.Description); // what is returned compiles
            }
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"3000 mutated files took {watch.Elapsed}");
        }
    }

    [Fact]
    public void HugeCounts_AreRefusedBeforeAllocating()
    {
        var bytes = Square(true).Build();
        // The bone count (first int of the first section's body).
        var copy = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(40), int.MaxValue);
        Assert.False(new RfcFormat().TryRead(copy, "bones", out _, out var error));
        Assert.Contains("bones", error!.Message);
        // A section length past the end.
        copy = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(36), int.MaxValue);
        Assert.False(new RfcFormat().TryRead(copy, "length", out _, out error));
        Assert.NotNull(error!.Offset);
    }

    // ── Real files ──────────────────────────────────────────────────────────

    /// <summary>Every .rfm/.rfc in the configured PS2 folders: packfiles and loose files.</summary>
    private static IEnumerable<(string Where, string Name, byte[] Bytes)> RealSamples()
    {
        if (LocalPaths.Ps2Directory is { } ps2)
        {
            foreach (var vpp in Directory.EnumerateFiles(ps2, "*.vpp"))
            {
                var archive = VppArchive.Open(vpp);
                foreach (var e in archive.Entries.Where(e => IsPs2Mesh(e.Name)))
                    yield return (Path.GetFileName(vpp), e.Name, archive.ReadEntry(e));
            }
            foreach (var file in Directory.EnumerateFiles(ps2, "*.*", SearchOption.AllDirectories).Where(IsPs2Mesh))
                yield return ("loose", Path.GetFileName(file), File.ReadAllBytes(file));
        }
        if (LocalPaths.MeshesStuffDirectory is { } stuff)
        {
            foreach (var file in Directory.EnumerateFiles(stuff, "*.*", SearchOption.AllDirectories).Where(IsPs2Mesh))
                yield return ("stuff", Path.GetFileName(file), File.ReadAllBytes(file));
        }
    }

    private static bool IsPs2Mesh(string name) =>
        name.EndsWith(".rfm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void RealFiles_ReadBuildAndReadBack()
    {
        int files = 0, rfc = 0, notes = 0;
        foreach (var (where, name, bytes) in RealSamples())
        {
            bool character = name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase);
            ILegacyMeshFormat format = character ? new RfcFormat() : new RfmFormat();
            Assert.True(format.Recognises(bytes), $"{where}|{name} recognised");
            Assert.True(format.TryRead(bytes, name, out var mesh, out var error), $"{where}|{name}: {error?.Message}");
            var d = mesh!.Description;
            Assert.Equal(character ? V3dKind.Character : V3dKind.StaticMesh, d.Kind);
            Assert.DoesNotContain(mesh.Notes, n => n.Contains("Skipped") || n.Contains("Ignored") || n.Contains("do not know"));
            var built = V3dBuilder.Build(d);
            var back = V3dReader.Read(V3dWriter.Write(built), Path.ChangeExtension(name, character ? ".v3c" : ".v3m"));
            Assert.Equal(d.Submeshes.Length, back.Submeshes.Count());
            Assert.Equal(d.Submeshes.Sum(s => s.Lods.Sum(l => l.Groups.Sum(g => g.Triangles.Length))),
                back.Submeshes.Sum(s => s.Lods.Sum(l => l.Batches.Sum(b => b.TriangleCount))));
            Assert.Equal(d.Bones.Length, back.Bones.Length);
            files++;
            if (character) rfc++;
            notes += mesh.Notes.Length;
        }
        output.WriteLine(files == 0
            ? "skipped: " + LocalPaths.HowToSet(LocalPaths.Ps2DirectoryVariable, "ps2Directory")
            : $"{files} PS2 meshes ({rfc} characters) read, compiled and read back; {notes} notes");
        if (LocalPaths.Ps2Directory is not null) Assert.True(files >= 600, $"{files} files");
    }

    [Fact]
    public void RedFactionII_FilesAreAllRefused()
    {
        if (LocalPaths.Rf2Directory is not { } dir)
        {
            output.WriteLine("skipped: " + LocalPaths.HowToSet(LocalPaths.Rf2DirectoryVariable, "rf2Directory"));
            return;
        }
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).Where(IsPs2Mesh))
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length < 8) continue;
            ILegacyMeshFormat format = file.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase) ? new RfcFormat() : new RfmFormat();
            Assert.False(format.Recognises(bytes), file);
            Assert.False(format.TryRead(bytes, Path.GetFileName(file), out _, out var error), file);
            Assert.Contains("Red Faction II mesh", error!.Message);
            count++;
        }
        output.WriteLine($"{count} Red Faction II meshes refused");
    }

    // ── Exporter twins (.v3d / .vcm in RF_PS2.VPP) ──────────────────────────

    /// <summary>A minimal reader of the exporter layout, enough to compare against (stock data only).</summary>
    private sealed class Twin
    {
        public sealed record Tri(Vector3[] P, Vector2[] Uv, string Texture, bool TwoSided);

        public List<(string Name, List<Vector3> Positions, List<Tri> Tris)> Submeshes { get; } = [];
        public List<(string Name, Quaternion Rot, Vector3 Pos, int Parent)> Bones { get; } = [];
        public List<(string Name, int Bone, Vector3 Pos, float Radius)> Spheres { get; } = [];
        public List<(string Name, int Parent, Quaternion Rot, Vector3 Pos)> Props { get; } = [];
        public List<byte[]> Weights { get; } = [];

        public static Twin Read(byte[] b)
        {
            var t = new Twin();
            int p = 40;
            while (true)
            {
                uint type = U32(b, p);
                int size = I32(b, p + 4);
                int start = p + 8;
                if (type == 0) break;
                if (type == 0x5355424D)
                {
                    string name = Str(b, start, 24);
                    int q = start + 48;
                    int nv = I32(b, q); q += 4;
                    var pos = Enumerable.Range(0, nv).Select(i => V3(b, q + 12 * i)).ToList(); q += 12 * nv;
                    int nn = I32(b, q); q += 4 + 16 * nn;
                    int nm = I32(b, q); q += 4;
                    var mats = Enumerable.Range(0, nm).Select(i => (Name: Str(b, q + 84 * i, 32), Flags: U32(b, q + 84 * i + 80))).ToList(); q += 84 * nm;
                    int nf = I32(b, q); q += 4;
                    var tris = new List<Tri>();
                    for (int i = 0; i < nf; i++, q += 52)
                    {
                        int m = I32(b, q + 48);
                        tris.Add(new Tri([pos[I32(b, q)], pos[I32(b, q + 4)], pos[I32(b, q + 8)]],
                            [new(F32(b, q + 24), F32(b, q + 28)), new(F32(b, q + 32), F32(b, q + 36)), new(F32(b, q + 40), F32(b, q + 44))],
                            mats[m].Name, (mats[m].Flags & 1) != 0));
                    }
                    t.Submeshes.Add((name, pos, tris));
                }
                else if (type == 0x424F4E45)
                {
                    for (int i = 0, n = I32(b, start); i < n; i++)
                    {
                        int q = start + 4 + 56 * i;
                        t.Bones.Add((Str(b, q, 24), new Quaternion(F32(b, q + 24), F32(b, q + 28), F32(b, q + 32), F32(b, q + 36)), V3(b, q + 40), I32(b, q + 52)));
                    }
                }
                else if (type == 0x43535048) t.Spheres.Add((Str(b, start, 24), I32(b, start + 24), V3(b, start + 28), F32(b, start + 40)));
                else if (type == 0x44554D42) t.Props.Add((Str(b, start, 24), I32(b, start + 24), new Quaternion(F32(b, start + 28), F32(b, start + 32), F32(b, start + 36), F32(b, start + 40)), V3(b, start + 44)));
                else if (type == 0x57414954)
                {
                    for (int i = 0, n = I32(b, start); i < n; i++) t.Weights.Add(b[(start + 4 + 8 * i)..(start + 12 + 8 * i)]);
                }
                p = start + size;
            }
            return t;
        }

        private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
        private static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
        private static float F32(byte[] b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at));
        private static Vector3 V3(byte[] b, int at) => new(F32(b, at), F32(b, at + 4), F32(b, at + 8));
        private static string Str(byte[] b, int at, int size)
        {
            var span = b.AsSpan(at, size);
            int nul = span.IndexOf((byte)0);
            return Encoding.Latin1.GetString(nul < 0 ? span : span[..nul]);
        }
    }

    private readonly record struct PosKey(int X, int Y, int Z)
    {
        public static PosKey Of(Vector3 v) => new(BitConverter.SingleToInt32Bits(v.X + 0f), BitConverter.SingleToInt32Bits(v.Y + 0f), BitConverter.SingleToInt32Bits(v.Z + 0f));
    }

    /// <summary>
    /// The position set of a triangle, independent of order, on a 0.1 mm grid: the PS2 tool and the PC
    /// compiler sometimes differ from the source in the last bit of a coordinate.
    /// </summary>
    private static string SetKey(IEnumerable<Vector3> p) =>
        string.Join("|", p.Select(v => FormattableString.Invariant($"{MathF.Round(v.X, 4)},{MathF.Round(v.Y, 4)},{MathF.Round(v.Z, 4)}")).Order(StringComparer.Ordinal));

    /// <summary>Equal within a few float steps of the coordinates involved (models here are under 20 m).</summary>
    private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= 1e-5f;

    /// <summary>
    /// Every .rfm/.rfc in RF_PS2.VPP that has its exporter source (.v3d / .vcm) beside it: each stored level
    /// must be one of the source's submeshes, triangle for triangle (same positions, winding, texture and
    /// two-sidedness; UVs equal up to a whole-number shift per face, which the compilers apply), and a
    /// character's skeleton, collision spheres, prop points, weights and source vertex order must agree.
    /// </summary>
    [Fact]
    public void ExporterTwins_Match()
    {
        if (LocalPaths.Ps2Directory is not { } ps2 || !File.Exists(Path.Combine(ps2, "RF_PS2.VPP")))
        {
            output.WriteLine("skipped: " + LocalPaths.HowToSet(LocalPaths.Ps2DirectoryVariable, "ps2Directory"));
            return;
        }
        var archive = VppArchive.Open(Path.Combine(ps2, "RF_PS2.VPP"));
        int compared = 0, full = 0, noTwin = 0, tris = 0, matchedTris = 0, shifted = 0, levels = 0, fullLevels = 0;
        int rfc = 0, rfcFull = 0, weightVertices = 0, weightOff = 0, morphSame = 0, morphTotal = 0, skeletonMismatches = 0;
        float worstWeight = 0f;
        var mismatches = new List<string>();
        foreach (var entry in archive.Entries.Where(e => IsPs2Mesh(e.Name)))
        {
            bool character = entry.Name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase);
            string twinName = Path.ChangeExtension(entry.Name, character ? ".vcm" : ".v3d");
            if (!archive.TryGetEntry(twinName, out var twinEntry))
            {
                noTwin++;
                continue;
            }
            var mesh = Read(archive.ReadEntry(entry), entry.Name).Description;
            var twin = Twin.Read(archive.ReadEntry(twinEntry));
            compared++;
            bool fileOk = true;
            var problems = new List<string>();

            foreach (var sub in mesh.Submeshes)
            {
                foreach (var lod in sub.Lods)
                {
                    levels++;
                    var mine = lod.Groups.SelectMany(g => g.Triangles.Select(t => (
                        P: new[] { g.Vertices[t.A].Position, g.Vertices[t.B].Position, g.Vertices[t.C].Position },
                        Uv: new[] { g.Vertices[t.A].TexCoord, g.Vertices[t.B].TexCoord, g.Vertices[t.C].TexCoord },
                        Texture: sub.Materials[g.Material].DiffuseMap.Text, t.IsDoubleSided))).ToList();
                    tris += mine.Count;
                    int best = -1, bestSubmesh = -1, bestShifted = 0;
                    for (int j = 0; j < twin.Submeshes.Count; j++)
                    {
                        var pool = twin.Submeshes[j].Tris.GroupBy(t => SetKey(t.P)).ToDictionary(g => g.Key, g => g.ToList());
                        int hits = 0, shifts = 0;
                        foreach (var t in mine)
                        {
                            if (!pool.TryGetValue(SetKey(t.P), out var candidates)) continue;
                            int found = candidates.FindIndex(c => Same(t.P, t.Uv, t.Texture, t.IsDoubleSided, c, out _));
                            if (found < 0) continue;
                            Same(t.P, t.Uv, t.Texture, t.IsDoubleSided, candidates[found], out bool wasShifted);
                            candidates.RemoveAt(found);
                            hits++;
                            if (wasShifted) shifts++;
                        }
                        if (hits > best) (best, bestSubmesh, bestShifted) = (hits, j, shifts);
                    }
                    matchedTris += Math.Max(0, best);
                    shifted += bestShifted;
                    bool levelOk = bestSubmesh >= 0 && best == mine.Count && twin.Submeshes[bestSubmesh].Tris.Count == mine.Count;
                    if (levelOk) fullLevels++;
                    else
                    {
                        fileOk = false;
                        problems.Add(bestSubmesh < 0 ? "no source submesh" : $"{best}/{mine.Count} triangles match '{twin.Submeshes[bestSubmesh].Name}' ({twin.Submeshes[bestSubmesh].Tris.Count})");
                    }
                }
            }

            // Prop points and collision spheres, as stored.
            if (!mesh.PropPoints.Select(p => (p.Name.Text, p.ParentIndex, p.Rotation, p.Position)).SequenceEqual(twin.Props))
            {
                fileOk = false;
                problems.Add("prop points differ");
            }
            if (!mesh.CollisionSpheres.Select(s => (s.Name.Text, s.BoneIndex, s.Position, s.Radius)).SequenceEqual(twin.Spheres))
            {
                fileOk = false;
                problems.Add("collision spheres differ");
                skeletonMismatches++;
            }

            if (character)
            {
                rfc++;
                if (!mesh.Bones.Select(b => (b.Name.Text, b.Rotation, b.Position, b.ParentIndex)).SequenceEqual(twin.Bones))
                {
                    fileOk = false;
                    problems.Add("bones differ");
                    skeletonMismatches++;
                }
                // The source vertex order (what morph keys address) is the exporter's position order; source
                // vertices no chunk uses are NaN.
                var source = twin.Submeshes[0];
                var originals = mesh.Submeshes[0].Lods[0].MorphVertices!.Value;
                var bySource = new Dictionary<PosKey, List<int>>();
                for (int o = 0; o < originals.Length; o++)
                {
                    if (float.IsNaN(originals[o].X)) continue;
                    morphTotal++;
                    if (o < source.Positions.Count && Near(originals[o], source.Positions[o])) morphSame++;
                    var key = PosKey.Of(originals[o]);
                    if (!bySource.TryGetValue(key, out var list)) bySource[key] = list = [];
                    list.Add(o);
                }
                // Weights: every vertex against its source vertex (8-bit there, 4-bit here).
                foreach (var v in mesh.Submeshes[0].Lods[0].Groups.SelectMany(g => g.Vertices))
                {
                    if (!bySource.TryGetValue(PosKey.Of(v.Position), out var indices)) continue;
                    float diff = indices.Where(i => i < twin.Weights.Count).Select(i => WeightDifference(v.Influences, twin.Weights[i])).DefaultIfEmpty(1f).Min();
                    weightVertices++;
                    worstWeight = Math.Max(worstWeight, diff);
                    if (diff > 1f / 16) weightOff++;
                }
                if (fileOk) rfcFull++;
            }

            if (fileOk) full++;
            else mismatches.Add($"{entry.Name}: {string.Join("; ", problems)}");
        }

        output.WriteLine($"{compared} PS2 meshes with an exporter twin ({rfc} characters), {noTwin} without; {full} match completely ({rfcFull} characters)");
        output.WriteLine($"levels: {fullLevels}/{levels} match a source submesh exactly; triangles: {matchedTris}/{tris} found ({shifted} with UVs shifted by whole numbers)");
        output.WriteLine($"weights: {weightVertices} vertices, largest difference {worstWeight:0.0000}, {weightOff} over 1/16; source vertex order: {morphSame}/{morphTotal}");
        foreach (var m in mismatches) output.WriteLine("  " + m);
        if (compared == 0) return;
        Assert.True(compared >= 145, $"{compared} twins");
        // The misses (measured): three PS2 files are different revisions of the model (fp_glock_shell's
        // texture, spotlight02, powerup_repairpack's second level), and eight have textures that a
        // one-sided and a two-sided material share in a way the chunk order cannot settle.
        Assert.True(full >= compared - 11, $"{full} of {compared} match completely");
        Assert.True(matchedTris >= tris * 0.99, $"{matchedTris} of {tris} triangles");
        Assert.Equal(0, skeletonMismatches);
        Assert.Equal(0, weightOff);
        Assert.Equal(morphTotal, morphSame);
        Assert.True(worstWeight <= 1f / 16 + 1e-4f, $"largest weight difference {worstWeight}");

        static bool Same(Vector3[] p, Vector2[] uv, string texture, bool twoSided, Twin.Tri c, out bool shifted)
        {
            shifted = false;
            if (!string.Equals(texture, c.Texture, StringComparison.OrdinalIgnoreCase) || twoSided != c.TwoSided) return false;
            for (int k = 0; k < 3; k++)
            {
                if (!Near(p[0], c.P[k]) || !Near(p[1], c.P[(k + 1) % 3]) || !Near(p[2], c.P[(k + 2) % 3])) continue;
                var d = uv[0] - c.Uv[k];
                var whole = new Vector2(MathF.Round(d.X), MathF.Round(d.Y));
                bool ok = true;
                for (int i = 0; i < 3; i++) ok &= Vector2.Distance(uv[i] - whole, c.Uv[(k + i) % 3]) < 1e-4f;
                if (!ok) continue;
                shifted = whole != Vector2.Zero;
                return true;
            }
            return false;
        }

        static float WeightDifference(IReadOnlyList<V3dBoneInfluence> mine, byte[] packed)
        {
            var theirs = new Dictionary<int, float>();
            for (int s = 0; s < 4; s++)
            {
                if (packed[2 * s + 1] != 0xFF && packed[2 * s] > 0) theirs[packed[2 * s + 1]] = theirs.GetValueOrDefault(packed[2 * s + 1]) + packed[2 * s] / 255f;
            }
            // A few exporter vertices sum to more than 255 (two bones at 255 each); compilers normalise them.
            float total = theirs.Values.Sum();
            if (total > 0) theirs = theirs.ToDictionary(kv => kv.Key, kv => kv.Value / total);
            var ours = mine.GroupBy(i => i.Bone).ToDictionary(g => g.Key, g => g.Sum(i => i.Weight));
            return theirs.Keys.Union(ours.Keys).Select(k => MathF.Abs(theirs.GetValueOrDefault(k) - ours.GetValueOrDefault(k))).DefaultIfEmpty(0f).Max();
        }
    }

    /// <summary>
    /// The .v3m files compiled on PC from the same sources (the demo disc's work\meshes): positions, UVs,
    /// winding, offsets and double-sided flags must agree exactly.
    /// </summary>
    [Fact]
    public void CompiledTwins_MatchExactly()
    {
        if (LocalPaths.Ps2Directory is not { } ps2 || !File.Exists(Path.Combine(ps2, "RF_PS2.VPP")))
        {
            output.WriteLine("skipped: " + LocalPaths.HowToSet(LocalPaths.Ps2DirectoryVariable, "ps2Directory"));
            return;
        }
        string work = Path.Combine(ps2, "work", "meshes");
        if (!Directory.Exists(work)) return;
        var archive = VppArchive.Open(Path.Combine(ps2, "RF_PS2.VPP"));
        int matched = 0, matchedLevels = 0;
        foreach (var v3m in Directory.EnumerateFiles(work, "*.v3m"))
        {
            string rfm = Path.GetFileNameWithoutExtension(v3m) + ".rfm";
            if (!archive.TryGetEntry(rfm, out var entry)) continue;
            var pc = V3dReader.ReadFile(v3m);
            var ps = Read(archive.ReadEntry(entry), rfm).Description;
            var pcSubs = pc.Submeshes.ToList();
            int pcTris = pcSubs.Sum(s => s.Lods[0].Batches.Sum(b => b.TriangleCount));
            int psTris = ps.Submeshes.Sum(s => s.Lods[0].Groups.Sum(g => g.Triangles.Length));
            if (pcTris != psTris)
            {
                output.WriteLine($"{rfm}: a different model ({psTris} triangles, the .v3m has {pcTris}); not compared");
                continue;
            }
            Assert.Equal(pcSubs.Count, ps.Submeshes.Length);
            for (int s = 0; s < pcSubs.Count; s++)
            {
                Assert.Equal(pcSubs[s].Offset, ps.Submeshes[s].Offset);
                Assert.Equal(pcSubs[s].Lods.Length, ps.Submeshes[s].Lods.Length);
                Assert.Equal(pcSubs[s].Materials.Select(m => (m.DiffuseMap.Text, m.Flags)), ps.Submeshes[s].Materials.Select(m => (m.DiffuseMap.Text, m.Flags)));
                for (int l = 0; l < pcSubs[s].Lods.Length; l++)
                {
                    var lod = pcSubs[s].Lods[l];
                    var pool = lod.Batches
                        .SelectMany(b => b.Triangles.Select(t => (P: new[] { b.Positions[t.A], b.Positions[t.B], b.Positions[t.C] }, Uv: new[] { b.TexCoords[t.A], b.TexCoords[t.B], b.TexCoords[t.C] }, t.Flags)))
                        .GroupBy(t => SetKey(t.P)).ToDictionary(g => g.Key, g => g.ToList());
                    var groups = ps.Submeshes[s].Lods[l].Groups;
                    Assert.Equal(lod.Batches.Sum(b => b.TriangleCount), groups.Sum(g => g.Triangles.Length));
                    int same = 0, total = 0;
                    foreach (var g in groups)
                    {
                        foreach (var t in g.Triangles)
                        {
                            total++;
                            var p = new[] { g.Vertices[t.A].Position, g.Vertices[t.B].Position, g.Vertices[t.C].Position };
                            var uv = new[] { g.Vertices[t.A].TexCoord, g.Vertices[t.B].TexCoord, g.Vertices[t.C].TexCoord };
                            if (!pool.TryGetValue(SetKey(p), out var candidates)) continue;
                            int found = candidates.FindIndex(c => c.Flags == t.Flags && Enumerable.Range(0, 3).Any(k =>
                                Enumerable.Range(0, 3).All(i => Near(p[i], c.P[(k + i) % 3]) && Vector2.Distance(uv[i], c.Uv[(k + i) % 3]) <= 1e-5f)));
                            if (found < 0) continue;
                            candidates.RemoveAt(found);
                            same++;
                        }
                    }
                    // The most detailed level must match; a lower level may be a different revision of the
                    // model on the PS2 (powerup_repairpack's second level has other UVs than its source).
                    if (l == 0) Assert.True(same == total, $"{rfm}: {same} of {total} triangles have the .v3m's positions, winding, UVs and flags");
                    else if (same != total) output.WriteLine($"{rfm} level {l}: {same} of {total} triangles match (a different revision of that level)");
                    if (same == total) matchedLevels++;
                }
            }
            matched++;
        }
        output.WriteLine($"{matched} compiled .v3m twins match (positions and UVs within 1e-5, winding, flags, offsets, materials); {matchedLevels} levels exactly");
        Assert.True(matched >= 7, $"{matched} twins matched");
    }
}
