using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Cairn.Formats.Rf2;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Red Faction II meshes (<c>.rfm</c>/<c>.rfc</c> version 0x114) read through <see cref="RfmFormat"/> and
/// <see cref="RfcFormat"/>: synthetic files built here (no game bytes), malformed and truncated input, the texture
/// names and packs, and — when the RF2 install is configured in research/local-paths.json — the real meshes (a fixed
/// set; every one with CAIRN_RF2_ALL_MESHES=1).
/// </summary>
public class Rf2MeshTests(ITestOutputHelper output)
{
    static Rf2MeshTests() => LegacyMeshSupport.EnsureRegistered();

    // ── Synthetic files ─────────────────────────────────────────────────────

    /// <summary>One geometry: its own vertices (static), its triangles (vertex, u, v per corner), texture and material.</summary>
    internal sealed record SynthGeometry(int Texture, int Material, (int V, float U, float W)[] Corners, (Vector3 P, Vector3 N)[]? Vertices = null, float Unknown = 0f);

    internal sealed record SynthMaterial(string Texture, uint Flags = 1, string Environment = "");

    internal sealed record SynthSkinVertex(Vector3 P, Vector3 N, int Source, (int Bone, float Weight)[] Weights);

    internal sealed class SynthLod
    {
        public string Name { get; init; } = "obj";
        public string Parent { get; init; } = "None";
        public List<string> Textures { get; } = [];
        public List<SynthMaterial> Materials { get; } = [];
        public List<SynthGeometry> Geometries { get; } = [];
        public Vector3 Offset { get; init; }
        public List<SynthSkinVertex> Skin { get; } = [];
        public int SourceCount { get; init; } = -1;
    }

    internal sealed class SynthRf2
    {
        public int Version { get; init; } = 0x114;
        public bool Skinned { get; init; }
        public List<(string Name, int Parent, Vector3 P, float R)> Spheres { get; } = [];
        public List<(string Name, int Parent, Vector3 P, Quaternion Q)> Props { get; } = [];
        public List<(string Name, int Parent, float[] M, Vector3 T)> Bones { get; } = [];
        public List<(float[] Distances, List<SynthLod> Lods)> Objects { get; } = [];

        public byte[] Build()
        {
            var b = new List<byte>();
            void U32(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); b.AddRange(s.ToArray()); }
            void I32(int v) => U32((uint)v);
            void F(float v) => I32(BitConverter.SingleToInt32Bits(v));
            void V(Vector3 v) { F(v.X); F(v.Y); F(v.Z); }
            void Z(string s) { b.AddRange(Encoding.Latin1.GetBytes(s)); b.Add(0); }
            void N65(string s)
            {
                b.Add(65); b.Add(0);
                var buf = new byte[65];
                Encoding.Latin1.GetBytes(s).CopyTo(buf, 0);
                if (s.Length + 2 < 65) buf[s.Length + 1] = 0xCD; // leftover memory after the terminator
                b.AddRange(buf);
            }
            U32(0x87128712); I32(Version); b.Add((byte)(Skinned ? 1 : 0));
            I32(Spheres.Count);
            foreach (var s in Spheres) { Z(s.Name); I32(s.Parent); V(s.P); F(s.R); }
            I32(Props.Count);
            foreach (var p in Props) { Z(p.Name); I32(p.Parent); V(p.P); F(p.Q.X); F(p.Q.Y); F(p.Q.Z); F(p.Q.W); }
            I32(Bones.Count);
            if (Skinned) foreach (var bone in Bones) { Z(bone.Name); I32(bone.Parent); foreach (float f in bone.M) F(f); V(bone.T); }
            I32(Objects.Count); I32(Objects.Count == 0 ? 0 : Objects.Max(o => o.Lods.Count));
            foreach (var o in Objects) { I32(o.Distances.Length); foreach (float d in o.Distances) F(d); }
            foreach (var lod in Objects.SelectMany(o => o.Lods)) { Z(lod.Name); Z(lod.Parent); U32(0); U32((uint)lod.Materials.Count); }
            foreach (var lod in Objects.SelectMany(o => o.Lods))
            {
                I32(lod.Textures.Count);
                foreach (var t in lod.Textures) Z(t);
                U32((uint)lod.Materials.Count);
                foreach (var m in lod.Materials)
                {
                    U32(0); U32(m.Flags); b.Add(0); U32(0xFFFFFFFF); U32(2);
                    b.Add(1); U32(0x12345678); N65(m.Texture); U32(0); F(1); U32(0);
                    b.Add(0);
                    U32(15); I32(1); F(0.5f); F(0); F(0); F(0); N65(m.Environment); U32(0xFFFFFFFF); I32(0); F(0); I32(2); F(1); F(2);
                }
                I32(lod.Geometries.Count);
                foreach (var g in lod.Geometries)
                {
                    if (!Skinned)
                    {
                        I32(g.Vertices!.Length);
                        foreach (var (p, n) in g.Vertices) { V(p); V(n); }
                    }
                    I32(g.Corners.Length / 3);
                    foreach (var (v, u, w) in g.Corners) { I32(v); F(u); F(w); }
                    I32(g.Texture); I32(g.Material); F(g.Unknown);
                    if (Skinned) U32(0xCDCDCDCD);
                }
                V(lod.Offset); F(1); V(new Vector3(-1)); V(new Vector3(1));
                if (Skinned)
                {
                    I32(lod.Skin.Count); I32(lod.SourceCount);
                    foreach (var v in lod.Skin)
                    {
                        V(v.P); V(v.N);
                        if (lod.SourceCount != -1) { b.Add((byte)v.Source); b.Add((byte)(v.Source >> 8)); }
                        for (int s = 0; s < 4; s++)
                        {
                            if (s < v.Weights.Length) { I32(v.Weights[s].Bone); F(v.Weights[s].Weight); }
                            else { I32(0xFF); F(0); }
                        }
                    }
                }
            }
            return [.. b];
        }
    }

    private static readonly float[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    /// <summary>
    /// A static crate: one object with two LODs (switch at 12 m), LOD 0 a quad of two geometries with different
    /// textures whose shared corners have different UVs, placed at (0, 0.5, 0).
    /// </summary>
    internal static SynthRf2 StaticCrate()
    {
        var up = Vector3.UnitY;
        (Vector3, Vector3)[] quad = [(new(-1, 0, -1), up), (new(1, 0, -1), up), (new(1, 0, 1), up), (new(-1, 0, 1), up)];
        var lod0 = new SynthLod { Name = "crate", Offset = new Vector3(0, 0.5f, 0) };
        lod0.Textures.AddRange(["crate_side.tga", "crate_top.tga"]);
        lod0.Materials.AddRange([new SynthMaterial("crate_side.tga"), new SynthMaterial("crate_top.tga", 9, "Chrome.tga")]);
        // winding agrees with the normals: cross(B-A, C-A) points along +Y
        lod0.Geometries.Add(new SynthGeometry(0, 0, [(0, 0, 0), (2, 1, 1), (1, 1, 0)], quad));
        lod0.Geometries.Add(new SynthGeometry(1, 1, [(0, 0.5f, 0.5f), (3, 0, 1), (2, 1, 1)], quad, Unknown: 0.05f));
        var lod1 = new SynthLod { Name = "crate", Offset = new Vector3(0, 0.5f, 0) };
        lod1.Textures.Add("crate_side.tga");
        lod1.Materials.Add(new SynthMaterial("crate_side.tga"));
        lod1.Geometries.Add(new SynthGeometry(0, 0, [(0, 0, 0), (2, 1, 1), (1, 1, 0)], quad));
        var mesh = new SynthRf2();
        mesh.Spheres.Add(("csphere_crate", -1, new Vector3(0, 0.5f, 0), 1.2f));
        mesh.Props.Add(("smoke", -1, new Vector3(0, 1, 0), Quaternion.Identity));
        mesh.Objects.Add(([12f, float.MaxValue], [lod0, lod1]));
        return mesh;
    }

    /// <summary>
    /// A two-bone character: a root and an arm turned 90 degrees about Y (stored model-to-bone), a triangle bound to
    /// both, a sphere and a prop on the arm; with or without the welded source indices.
    /// </summary>
    internal static SynthRf2 Character(bool sourceIndices)
    {
        var mesh = new SynthRf2 { Skinned = true };
        mesh.Bones.Add(("root", -1, Identity, Vector3.Zero));
        // model-to-bone rotation: 90 degrees about Y as a row-major matrix (x_bone = M x + t)
        float[] turn = [0, 0, -1, 0, 1, 0, 1, 0, 0];
        mesh.Bones.Add(("arm", 0, turn, new Vector3(0, -1, 0)));
        mesh.Spheres.Add(("csphere_arm", 1, new Vector3(0, 0.1f, 0), 0.25f));
        mesh.Props.Add(("primary_weapon", 1, new Vector3(0.2f, 0, 0), Quaternion.Identity));
        var lod = new SynthLod { Name = "guard", SourceCount = sourceIndices ? 3 : -1 };
        lod.Textures.Add("guard_body.tga");
        lod.Materials.Add(new SynthMaterial("guard_body.tga"));
        lod.Geometries.Add(new SynthGeometry(0, 0, [(0, 0, 0), (2, 0, 1), (1, 1, 0), (2, 0, 1), (3, 1, 1), (1, 1, 0)]));
        var n = Vector3.UnitY;
        lod.Skin.Add(new SynthSkinVertex(new(0, 1, 0), n, 0, [(0, 1f)]));
        lod.Skin.Add(new SynthSkinVertex(new(1, 1, 0), n, 1, [(0, 0.5f), (1, 0.5f)]));
        lod.Skin.Add(new SynthSkinVertex(new(0, 1, 1), n, 2, [(1, 0.6f), (0, 0.3f)])); // sums to 0.9
        lod.Skin.Add(new SynthSkinVertex(new(1, 1, 1), n, 2, [(1, 1f)]));
        mesh.Objects.Add(([float.MaxValue], [lod]));
        return mesh;
    }

    private static LegacyMesh Read(byte[] bytes, string name)
    {
        var format = LegacyMeshSupport.Identify(bytes, name);
        Assert.NotNull(format);
        Assert.True(format!.TryRead(bytes, name, out var mesh, out var error), error?.Message);
        return mesh!;
    }

    [Fact]
    public void StaticMesh_ReadsObjectsLodsMaterialsAndSplitsCornersByUv()
    {
        var bytes = StaticCrate().Build();
        Assert.True(new RfmFormat().Recognises(bytes));
        Assert.False(new RfcFormat().Recognises(bytes));
        Assert.True(LegacyMeshSupport.IsRedFactionII(bytes));
        var mesh = Read(bytes, "crate.rfm");
        Assert.True(mesh.IsRedFactionII);
        Assert.Equal("rfm", mesh.SourceFormat);
        Assert.Equal("Red Faction II static mesh", LegacyMeshSupport.FormatName(mesh));
        var d = mesh.Description;
        Assert.Equal(V3dKind.StaticMesh, d.Kind);
        var sub = Assert.Single(d.Submeshes);
        Assert.Equal("crate", sub.Name.Text);
        Assert.Equal(new Vector3(0, 0.5f, 0), sub.Offset);
        Assert.Equal(2, sub.Lods.Length);
        Assert.Equal(0f, sub.Lods[0].Distance);
        Assert.Equal(12f, sub.Lods[1].Distance);
        Assert.Equal(["crate_side.tga", "crate_top.tga"], sub.Materials.Select(m => m.DiffuseMap.Text));
        Assert.Equal(0x1u, sub.Materials[0].Flags);
        Assert.Equal(0x9u, sub.Materials[1].Flags); // see-through flag kept
        Assert.Equal("Chrome.tga", sub.Materials[1].ReflectionMap.Text);
        // LOD 0: two groups; vertex 0 and 2 are used by both triangles, but vertex 0's UV differs: 3 + 3 vertices
        var groups = sub.Lods[0].Groups;
        Assert.Equal(2, groups.Length);
        Assert.Equal(3, groups[0].Vertices.Length);
        Assert.Equal(3, groups[1].Vertices.Length);
        Assert.Equal(new Vector2(0.5f, 0.5f), groups[1].Vertices[0].TexCoord);
        Assert.Equal(new Vector3(-1, 0, -1), groups[1].Vertices[0].Position);
        // the stored winding is kept
        var t = groups[0].Triangles[0];
        var v = groups[0].Vertices;
        Assert.True(Vector3.Dot(Vector3.Cross(v[t.B].Position - v[t.A].Position, v[t.C].Position - v[t.A].Position), Vector3.UnitY) > 0);
        Assert.Single(d.CollisionSpheres);
        Assert.Equal(-1, d.CollisionSpheres[0].BoneIndex);
        Assert.Equal("smoke", Assert.Single(d.PropPoints).Name.Text);
        Assert.Contains(mesh.Notes, n => n.Contains("12 m", StringComparison.Ordinal));
        Assert.Contains(mesh.Notes, n => n.Contains("environment map (Chrome.tga)", StringComparison.Ordinal));
        Assert.Contains(mesh.Notes, n => n.Contains("1 geometry stores a value", StringComparison.Ordinal));
        Assert.Contains(mesh.Notes, n => n.Contains("UVs are stored per triangle corner", StringComparison.Ordinal));

        var conversion = LegacyMeshSupport.Convert(bytes, "crate.rfm");
        Assert.Equal("crate.v3m", conversion.OutputName);
        var back = V3dReader.Read(conversion.Bytes, "crate.v3m");
        Assert.Equal(2, back.Submeshes.Single().Lods.Length);
        Assert.Empty(conversion.Textures);
        Assert.DoesNotContain(MeshLinter.Analyze(back, new MeshLintContext { FileName = "crate.v3m" }), x => x.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Character_ReadsSkeletonWeightsSpheresAndProps(bool sourceIndices)
    {
        var bytes = Character(sourceIndices).Build();
        Assert.True(new RfcFormat().Recognises(bytes));
        Assert.False(new RfmFormat().Recognises(bytes));
        var mesh = Read(bytes, "guard.rfc");
        Assert.Equal("Red Faction II character mesh", LegacyMeshSupport.FormatName(mesh));
        var d = mesh.Description;
        Assert.Equal(V3dKind.Character, d.Kind);
        Assert.Equal(2, d.Bones.Length);
        Assert.Equal(0, d.Bones[1].ParentIndex);
        Assert.Equal(Vector3.Zero, d.Submeshes.Single().Offset);

        // the arm's joint: the stored inverse bind is x_bone = M x + t, so the joint (x_bone = 0) is at -M^T t
        var skeleton = Cairn.Rfa.Animation.Skeleton.FromBones(d.Bones);
        var joint = skeleton.RestWorld[1].Position;
        Assert.True(Vector3.Distance(joint, new Vector3(0, 1, 0)) < 1e-5f, $"joint {joint}");
        // and any model-space point lands in bone space exactly where M x + t puts it
        var rest = skeleton.RestWorld[1];
        var modelPoint = new Vector3(0.3f, 1.2f, -0.4f);
        var m = new[] { 0f, 0, -1, 0, 1, 0, 1, 0, 0 };
        var expected = new Vector3(m[0] * modelPoint.X + m[1] * modelPoint.Y + m[2] * modelPoint.Z,
            m[3] * modelPoint.X + m[4] * modelPoint.Y + m[5] * modelPoint.Z,
            m[6] * modelPoint.X + m[7] * modelPoint.Y + m[8] * modelPoint.Z) + new Vector3(0, -1, 0);
        var inBone = rest.Inverse().TransformPoint(modelPoint);
        Assert.True(Vector3.Distance(inBone, expected) < 1e-5f, $"{inBone} vs {expected}");

        var group = d.Submeshes.Single().Lods.Single().Groups.Single();
        Assert.Equal(4, group.Vertices.Length);
        var shared = group.Vertices.Single(v => v.Position == new Vector3(1, 1, 0));
        Assert.Equal([(0, 0.5f), (1, 0.5f)], shared.Influences.Select(i => (i.Bone, i.Weight)));
        Assert.Equal(1, d.CollisionSpheres.Single().BoneIndex);
        Assert.Equal(1, d.PropPoints.Single().ParentIndex);
        Assert.Contains(mesh.Notes, n => n.Contains("1 of the 2 bones are turned", StringComparison.Ordinal));
        Assert.Contains(mesh.Notes, n => n.Contains("1 vertices' weights did not add up to 1", StringComparison.Ordinal));
        var morph = d.Submeshes.Single().Lods.Single().MorphVertices;
        if (sourceIndices)
        {
            Assert.NotNull(morph);
            Assert.Equal(3, morph!.Value.Length);
            Assert.Equal(new Vector3(0, 1, 1), morph.Value[2]);
        }
        else
        {
            Assert.Null(morph);
        }

        var conversion = LegacyMeshSupport.Convert(bytes, "guard.rfc");
        Assert.Equal("guard.v3c", conversion.OutputName);
        var back = V3dReader.Read(conversion.Bytes, "guard.v3c");
        Assert.Equal(2, back.Bones.Length);
        Assert.DoesNotContain(MeshLinter.Analyze(back, new MeshLintContext { FileName = "guard.v3c" }), x => x.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MoreThanSevenTexturesInALod_SplitsAStaticObject()
    {
        var mesh = new SynthRf2();
        var lod = new SynthLod { Name = "busy" };
        var up = Vector3.UnitY;
        (Vector3, Vector3)[] tri = [(new(0, 0, 0), up), (new(0, 0, 1), up), (new(1, 0, 0), up)];
        for (int i = 0; i < 9; i++)
        {
            lod.Textures.Add($"tex{i}.tga");
            lod.Materials.Add(new SynthMaterial($"tex{i}.tga"));
            lod.Geometries.Add(new SynthGeometry(i, i, [(0, 0, 0), (1, 0, 1), (2, 1, 0)], tri));
        }
        mesh.Objects.Add(([float.MaxValue], [lod]));
        var read = Read(mesh.Build(), "busy.rfm");
        Assert.Equal(["busy", "busy_2"], read.Description.Submeshes.Select(s => s.Name.Text));
        Assert.Equal([7, 2], read.Description.Submeshes.Select(s => s.Materials.Length));
        Assert.Contains(read.Notes, n => n.Contains("more than 7 textures", StringComparison.Ordinal));
        var conversion = LegacyMeshSupport.Convert(mesh.Build(), "busy.rfm");
        Assert.DoesNotContain(MeshLinter.Analyze(conversion.Mesh, new MeshLintContext()), x => x.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Prototypes_AreRefusedWithAReason(int version)
    {
        var bytes = new SynthRf2 { Version = version }.Build();
        Assert.False(new RfmFormat().Recognises(bytes));
        Assert.False(new RfcFormat().Recognises(bytes));
        Assert.False(LegacyMeshSupport.TryRead(bytes, "proto.rfm", out var mesh, out var error));
        Assert.Null(mesh);
        Assert.StartsWith($"Red Faction II prototype mesh (version {version}): not supported", error!.Message);
        Assert.False(new RfcFormat().TryRead(bytes, "proto.rfc", out _, out error));
        Assert.Contains("prototype", error!.Message);
    }

    [Fact]
    public void NameAndContentDisagree_IsReadAsStoredWithANote()
    {
        // identified by content, a static mesh named .rfc is read as a static mesh ...
        Assert.Equal(V3dKind.StaticMesh, Read(StaticCrate().Build(), "named_wrong.rfc").Description.Kind);
        // ... and read through the .rfc reader itself, it says so
        Assert.True(new RfcFormat().TryRead(StaticCrate().Build(), "named_wrong.rfc", out var read, out _));
        Assert.Equal(V3dKind.StaticMesh, read!.Description.Kind);
        Assert.StartsWith("The file is a static mesh (.rfm)", read.Notes[0]);
    }

    [Fact]
    public void TruncatedAndDamagedFiles_GiveAnErrorAndNeverThrow()
    {
        foreach (var bytes in new[] { StaticCrate().Build(), Character(true).Build(), Character(false).Build() })
        {
            for (int cut = 8; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 97))
            {
                var part = bytes[..cut];
                Assert.False(LegacyMeshSupport.TryRead(part, "cut.rfm", out _, out var error), $"cut at {cut}");
                Assert.StartsWith("Red Faction II mesh", error!.Message);
            }
            var rng = new Random(1234);
            for (int i = 0; i < 300; i++)
            {
                var copy = (byte[])bytes.Clone();
                for (int k = 0; k < 4; k++) copy[rng.Next(9, copy.Length)] = (byte)rng.Next(256);
                if (LegacyMeshSupport.TryRead(copy, "fuzz.rfc", out var mesh, out _))
                {
                    try { LegacyMeshSupport.Convert(mesh!, "fuzz.rfc"); }
                    catch (AssetFormatException) { }
                }
            }
        }
        // a count far beyond the file is refused before anything is allocated
        var huge = StaticCrate().Build();
        BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(9), int.MaxValue);
        Assert.False(LegacyMeshSupport.TryRead(huge, "huge.rfm", out _, out var why));
        Assert.Contains("does not fit", why!.Message);
    }

    [Fact]
    public void ABadBoneParent_IsRefused()
    {
        var mesh = Character(true);
        mesh.Bones[1] = mesh.Bones[1] with { Parent = 7 };
        Assert.False(LegacyMeshSupport.TryRead(mesh.Build(), "bad.rfc", out _, out var error));
        Assert.Contains("parent 7", error!.Message);
    }

    // ── Texture names and packs ─────────────────────────────────────────────

    [Fact]
    public void TextureNames_FollowRedux()
    {
        Assert.Equal(1782, Rf2TextureNames.All.Count);
        // a fixed pair wins over the suffix match
        var invisible = Rf2TextureNames.Translate("rck_invisible.tga");
        Assert.Equal(("rck_invisible04.tga", Rf2TextureMatch.Manual), (invisible.Translated, invisible.Match));
        // the first target ending with the name without its material prefix
        var suffix = Rf2TextureNames.All.Values.First(t => t.Match == Rf2TextureMatch.Suffix && !t.IsWeak);
        Assert.EndsWith(MaterialStripped(suffix.Original), suffix.Translated, StringComparison.OrdinalIgnoreCase);
        Assert.True(Rf2TextureNames.Translate("A.TGA").IsWeak, "a.tga has many candidates");
        Assert.Equal("cem_grf2081_brka.tga", Rf2TextureNames.Translate("a.tga").Translated);
        var none = Rf2TextureNames.Translate("not_an_rf2_texture_name.tga");
        Assert.Equal(Rf2TextureMatch.None, none.Match);
        Assert.False(none.IsTranslated);
        Assert.Equal("not_an_rf2_texture_name.tga", none.Translated);
        Assert.Equal("Rck_Invisible.tga", Rf2TextureNames.Translate("Rck_Invisible.tga").Original);
    }

    private static string MaterialStripped(string name) =>
        Rf2TextureNames.MaterialPrefixes.FirstOrDefault(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } p ? name[p.Length..] : name;

    [Fact]
    public void Convert_TranslatesKnownTextureNamesByDefault()
    {
        var mesh = StaticCrate();
        mesh.Objects[0].Lods[0].Textures[1] = "rck_invisible.tga";
        mesh.Objects[0].Lods[0].Materials[1] = new SynthMaterial("rck_invisible.tga");
        var conversion = LegacyMeshSupport.Convert(mesh.Build(), "crate.rfm");
        var names = conversion.Mesh.Submeshes.Single().Materials.Select(m => m.DiffuseMap.Text).ToList();
        Assert.Equal(["crate_side.tga", "rck_invisible04.tga"], names);
        Assert.Contains(conversion.Report, n => n.Contains("rck_invisible.tga → rck_invisible04.tga", StringComparison.Ordinal));
        Assert.Contains(conversion.Report, n => n.Contains("crate_side.tga", StringComparison.Ordinal) && n.Contains("no Red Faction translation", StringComparison.Ordinal));
        Assert.Contains(Rf2TextureNames.TextureSetNote, conversion.Report);
    }

    [Fact]
    public void Convert_KeepingOriginals_WritesTheTexturesFromThePegPacks()
    {
        byte[] peg = SyntheticPeg(("crate_side.tga", 4, 2), ("crate_top.tga", 2, 2));
        var siblings = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["crate_v3d.peg"] = peg, ["other.peg"] = SyntheticPeg(("crate_side.tga", 8, 8)) };
        var packs = LegacyMeshSupport.TexturePacksFor("crate.rfm", false, siblings.Keys, n => siblings.GetValueOrDefault(n), "test");
        Assert.Equal(["crate_v3d.peg", "other.peg"], packs.PackNames);
        Assert.Equal("crate_v3d.peg", packs.Find("CRATE_SIDE.vbm")?.Pack);
        var options = new LegacyConvertOptions { Rf2Textures = Rf2TextureMode.KeepOriginals, TexturePacks = packs };
        var conversion = LegacyMeshSupport.Convert(StaticCrate().Build(), "crate.rfm", options: options);
        Assert.Equal(["crate_side.tga", "crate_top.tga"], conversion.Mesh.Submeshes.Single().Materials.Select(m => m.DiffuseMap.Text));
        Assert.Equal(["crate_side.tga", "crate_top.tga"], conversion.Textures.Select(t => t.Name));
        var side = TgaCodec.Decode(conversion.Textures[0].Bytes, "crate_side.tga");
        Assert.Equal((4, 2), (side.Width, side.Height));
        Assert.Equal(["crate.v3m", "crate_side.tga", "crate_top.tga"], conversion.AllFiles.Select(f => f.Name));
        Assert.Contains(conversion.Report, n => n.Contains("2 textures were written beside the mesh as .tga from crate_v3d.peg", StringComparison.Ordinal));

        var missing = LegacyMeshSupport.Convert(StaticCrate().Build(), "crate.rfm", options: options with { TexturePacks = null });
        Assert.Empty(missing.Textures);
        Assert.Contains(missing.Report, n => n.Contains("no .peg texture packs were found", StringComparison.Ordinal));
    }

    [Fact]
    public void PegSearchOrder_PrefersTheMeshsOwnPackThenItsBaseModels()
    {
        string[] available = ["misc.peg", "burke_vcm.peg", "burke_b.peg", "Burke_B_vcm.PEG", "burke.peg", "readme.txt"];
        Assert.Equal(["Burke_B_vcm.PEG", "burke_b.peg", "burke_vcm.peg", "burke.peg", "misc.peg"],
            PegTextureSet.SearchOrder("burke_b.rfc", available, LegacyMeshSupport.TexturePackSuffixes(true)));
        Assert.Equal(["misc.peg"], PegTextureSet.SearchOrder("crate.rfm", ["misc.peg"], LegacyMeshSupport.TexturePackSuffixes(false)));
        // skins named after the mesh come next; a whole game's worth of unrelated packs is not searched
        var many = Enumerable.Range(0, 30).Select(i => $"level{i:00}.peg").Append("military_grunt_a.peg").Append("military_grnt_hi.peg").ToList();
        Assert.Equal(["military_grunt_a.peg", "military_grnt_hi.peg"], PegTextureSet.SearchOrder("military_Grunt.rfc", many, LegacyMeshSupport.TexturePackSuffixes(true)));
        Assert.Equal(32, PegTextureSet.SearchOrder("military_Grunt.rfc", many, LegacyMeshSupport.TexturePackSuffixes(true), maxOtherPacks: 100).Count);
    }

    [Fact]
    public void PegTextureSet_LayersTexturesIntoAResolver()
    {
        var set = new PegTextureSet("test", [new PegPack("a.peg", () => SyntheticPeg(("flag.vbm", 2, 2))), new PegPack("gone.peg", () => null)]);
        Assert.True(set.Offers("flag.tga"));
        Assert.False(set.Offers("flag.vbm"));
        var resolver = set.Layer(new AssetResolver(new AssetResolverOptions()));
        var hit = resolver.Resolve("FLAG.vbm");
        Assert.NotNull(hit);
        Assert.Equal("flag.tga", hit!.ResolvedName, ignoreCase: true);
        Assert.Equal("test", hit.DisplayLocation);
        Assert.Equal(2, TgaCodec.Decode(hit.ReadAllBytes(), "flag.tga").Width);
        Assert.Null(resolver.Resolve("nothing.tga"));
        Assert.Contains(set.Unreadable, u => u.Pack == "gone.peg");
    }

    /// <summary>A GEKV version 6 pack of 32-bit textures (grey ramps), as the RF2 packs store them.</summary>
    internal static byte[] SyntheticPeg(params (string Name, int W, int H)[] textures)
    {
        const int header = 32, entry = 64;
        int dataStart = header + textures.Length * entry;
        var offsets = new List<int>();
        int at = dataStart;
        foreach (var (_, w, h) in textures) { offsets.Add(at); at += (w * h * 4 + 15) & ~15; }
        var b = new byte[at];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x564B4547);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 6);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), textures.Length * entry);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), textures.Length);
        for (int i = 0; i < textures.Length; i++)
        {
            var (name, w, h) = textures[i];
            int e = header + i * entry;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(e), (ushort)w);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(e + 2), (ushort)h);
            b[e + 4] = 7; b[e + 7] = 1; b[e + 9] = 1;
            Encoding.Latin1.GetBytes(name).CopyTo(b, e + 12);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(e + 60), offsets[i]);
            for (int p = 0; p < w * h; p++)
            {
                int o = offsets[i] + p * 4;
                b[o] = b[o + 1] = b[o + 2] = (byte)(p * 255 / Math.Max(1, w * h - 1));
                b[o + 3] = 0x80;
            }
        }
        return b;
    }

    // ── Real files ──────────────────────────────────────────────────────────

    /// <summary>A fixed set from All_Levels: characters with turned bones, LODs, several objects, more than 7 textures.</summary>
    private static readonly string[] PackedSamples =
    [
        "tangier.rfc", "security_guard_b.rfc", "burke.rfc", "burke_b.rfc", "mech_suit.rfc", "5tontruck.rfm", "5tontruck_des.rfm",
        "firehydrant.rfm", "cable_spool.rfm", "fp_gat_body_l.rfm", "leaf_tree.rfm", "glass_screen.rfm", "ctf_flag.rfc", "fodder_cop_c.rfc",
    ];

    [Fact]
    public void RealMeshes_ReadConvertAndPassTheLinter()
    {
        if (LocalPaths.Rf2GameDirectory is not { } game)
        {
            output.WriteLine("skipped: " + LocalPaths.HowToSet(LocalPaths.Rf2GameDirectoryVariable, "rf2GameDirectory"));
            return;
        }
        bool all = Environment.GetEnvironmentVariable("CAIRN_RF2_ALL_MESHES") == "1";
        var files = new List<(string Name, Func<byte[]> Read, string Where)>();
        string media = Path.Combine(game, "pc_media");
        // loose groups: every mesh of Loading_screen and Animations_2, and (all) the rest
        string[] looseGroups = all ? ["Loading_screen", "Animations_2", "Botmatch_core", "Menu_extras"] : ["Loading_screen", "Animations_2"];
        foreach (string group in looseGroups)
        {
            string dir = Path.Combine(media, group);
            if (!Directory.Exists(dir)) continue;
            foreach (string f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).Where(IsMeshName))
                files.Add((Path.GetFileName(f), () => File.ReadAllBytes(f), group));
        }
        string[] packedGroups = all ? ["All_Levels", "Permanent_data"] : ["All_Levels"];
        foreach (string toc in packedGroups)
        {
            foreach (var (name, open) in PackedEntries(media, toc))
            {
                if (!IsMeshName(name)) continue;
                if (all || PackedSamples.Contains(name, StringComparer.OrdinalIgnoreCase)) files.Add((name, open, toc));
            }
        }
        if (!all) Assert.True(files.Count(f => f.Where == "All_Levels") >= PackedSamples.Length - 1, $"{files.Count} samples found");

        int read = 0, refused = 0, notes = 0, textureUses = 0, texturesFound = 0;
        var distinct = new HashSet<string>();
        // where the textures are: the packs beside each mesh in its group (only for the full run: it reads many packs)
        var groupEntries = new Dictionary<string, Dictionary<string, Func<byte[]>>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Func<byte[]>> EntriesOf(string where)
        {
            if (groupEntries.TryGetValue(where, out var known)) return known;
            var map = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
            string dir = Path.Combine(media, where);
            if (Directory.Exists(Path.Combine(dir)) && !File.Exists(Path.Combine(dir, where + ".packfile")))
                foreach (string f in Directory.EnumerateFiles(dir, "*.peg", SearchOption.AllDirectories)) map.TryAdd(Path.GetFileName(f), () => File.ReadAllBytes(f));
            else
                foreach (var (n, r) in PackedEntries(media, where)) map.TryAdd(n, r);
            return groupEntries[where] = map;
        }
        foreach (var (name, open, where) in files)
        {
            byte[] bytes = open();
            if (!distinct.Add(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)))) continue;
            int? version = bytes.Length >= 8 ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) : null;
            bool ok = LegacyMeshSupport.TryRead(bytes, name, out var mesh, out var error);
            if (version != 0x114)
            {
                Assert.False(ok, $"{where}/{name} (version {version}) should be refused");
                Assert.Contains("prototype mesh", error!.Message);
                refused++;
                continue;
            }
            Assert.True(ok, $"{where}/{name}: {error?.Message}");
            Assert.True(mesh!.IsRedFactionII);
            Assert.Equal(name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase), mesh.Description.Kind == V3dKind.Character);
            var entries = EntriesOf(where);
            var packs = LegacyMeshSupport.TexturePacksFor(name, mesh.Description.Kind == V3dKind.Character, entries.Keys, n => entries.TryGetValue(n, out var r) ? r() : null, where);
            var conversion = LegacyMeshSupport.Convert(mesh, name, new LegacyConvertOptions { TexturePacks = packs });
            var back = V3dReader.Read(conversion.Bytes, conversion.OutputName);
            Assert.True(mesh.Description.Kind != V3dKind.Character || back.Submeshes.Count() == 1, $"{where}/{name}: a character must convert to one submesh");
            var errors = MeshLinter.Analyze(back, new MeshLintContext { FileName = conversion.OutputName }).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, $"{where}/{name}: {string.Join("; ", errors.Select(e => e.Message))}");
            Assert.Equal(mesh.Description.Submeshes.Sum(s => s.Lods.Sum(l => l.Groups.Sum(g => g.Triangles.Length))),
                back.Submeshes.Sum(s => s.Lods.Sum(l => l.Batches.Sum(b => b.TriangleCount))));
            notes += conversion.Report.Length;
            read++;
            if (all)
            {
                foreach (string texture in mesh.Description.Submeshes.SelectMany(s => s.Materials).Select(m => m.DiffuseMap.Text).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    textureUses++;
                    if (packs.Contains(texture)) texturesFound++;
                }
            }
        }
        output.WriteLine($"{read} Red Faction II meshes read and converted, {refused} prototypes refused, {notes} report lines ({files.Count} files, {distinct.Count} distinct)");
        if (all) output.WriteLine($"textures: {texturesFound} of {textureUses} found in the .peg packs beside their mesh");
        Assert.True(read > 0);
        if (all) Assert.True(read >= 481, $"{read} of the 481 version 0x114 meshes");
    }

    [Fact]
    public void RealMesh_TexturesResolveFromItsPegPack()
    {
        if (LocalPaths.Rf2GameDirectory is not { } game) return;
        var entries = PackedEntries(Path.Combine(game, "pc_media"), "All_Levels").ToDictionary(e => e.Name, e => e.Read, StringComparer.OrdinalIgnoreCase);
        if (!entries.TryGetValue("5tontruck.rfm", out var readMesh)) return;
        var mesh = LegacyMeshSupport.Read(readMesh(), "5tontruck.rfm");
        var packs = LegacyMeshSupport.TexturePacksFor("5tontruck.rfm", false, entries.Keys, n => entries.TryGetValue(n, out var r) ? r() : null, "All_Levels");
        Assert.Equal("5tontruck_v3d.peg", packs.PackNames[0]);
        var textures = mesh.Description.Submeshes.SelectMany(s => s.Materials).Select(m => m.DiffuseMap.Text).Distinct().ToList();
        Assert.All(textures, t => Assert.NotNull(packs.Decode(t)));
        var conversion = LegacyMeshSupport.Convert(mesh, "5tontruck.rfm", new LegacyConvertOptions { Rf2Textures = Rf2TextureMode.KeepOriginals, TexturePacks = packs });
        Assert.Equal(textures.Count, conversion.Textures.Length);
        output.WriteLine(string.Join(Environment.NewLine, conversion.Report));
    }

    private static bool IsMeshName(string name) =>
        name.EndsWith(".rfm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The entries of a packed RF2 group (<c>&lt;name&gt;.toc_group</c> beside its folder): group name, folder, flags, a
    /// second value, the packfile path, the count, then name, size and offset per entry. A minimal reader for these
    /// tests only; nothing when the files are not there or not a packed group.
    /// </summary>
    internal static IEnumerable<(string Name, Func<byte[]> Read)> PackedEntries(string media, string group)
    {
        string toc = Path.Combine(media, group + ".toc_group"), pack = Path.Combine(media, group, group + ".packfile");
        if (!File.Exists(toc) || !File.Exists(pack)) yield break;
        byte[] t = File.ReadAllBytes(toc);
        int p = 0;
        string Z() { int e = Array.IndexOf(t, (byte)0, p); string s = Encoding.Latin1.GetString(t, p, e - p); p = e + 1; return s; }
        int I() { int v = BinaryPrimitives.ReadInt32LittleEndian(t.AsSpan(p)); p += 4; return v; }
        Z(); Z();
        int flags = I();
        I();
        if ((flags & 2) == 0) yield break;
        Z();
        int count = I();
        for (int i = 0; i < count; i++)
        {
            string name = Z();
            int size = I(), offset = I();
            yield return (name, () =>
            {
                using var fs = File.OpenRead(pack);
                fs.Position = offset;
                var buffer = new byte[size];
                fs.ReadExactly(buffer);
                return buffer;
            });
        }
    }
}
