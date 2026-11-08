using System.Collections.Immutable;
using System.Numerics;
using Cairn.Formats;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using static Cairn.Rfa.Tests.Rf2MeshTests;

namespace Cairn.Rfa.Tests;

/// <summary>
/// A <c>.v3c</c> must be one submesh (the game keeps only the last): Red Faction II characters convert to one, with an
/// atlas when they use too many textures; existing files are reported (V3C028) and can be merged. Synthetic meshes only.
/// </summary>
public class CharacterSubmeshTests
{
    static CharacterSubmeshTests() => LegacyMeshSupport.EnsureRegistered();

    private static readonly float[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    private sealed record Tex(string Name, float Uv = 1f, uint Flags = 1, string Environment = "", int Size = 16);

    private static Tex[] Textures(int count) => [.. Enumerable.Range(0, count).Select(i => new Tex($"tex{(char)('a' + i)}.tga"))];

    /// <summary>One triangle per texture, each on its own three vertices, all on the root bone.</summary>
    private static SynthLod Lod(string name, IEnumerable<Tex> textures)
    {
        var lod = new SynthLod { Name = name };
        int i = 0;
        foreach (var t in textures)
        {
            lod.Textures.Add(t.Name);
            lod.Materials.Add(new SynthMaterial(t.Name, t.Flags, t.Environment));
            int v = lod.Skin.Count;
            foreach (var p in new Vector3[] { new(i, 0, 0), new(i, 0, 1), new(i + 1, 0, 0) })
                lod.Skin.Add(new SynthSkinVertex(p, Vector3.UnitY, v, [(0, 1f)]));
            lod.Geometries.Add(new SynthGeometry(i, i, [(v, 0, 0), (v + 1, 0, t.Uv), (v + 2, t.Uv, 0)]));
            i++;
        }
        return lod;
    }

    private static SynthRf2 Character(params IEnumerable<Tex>[] objects)
    {
        var mesh = new SynthRf2 { Skinned = true };
        mesh.Bones.Add(("root", -1, Identity, Vector3.Zero));
        mesh.Props.Add(("muzzle_1", 0, Vector3.UnitX, Quaternion.Identity));
        for (int o = 0; o < objects.Length; o++) mesh.Objects.Add(([float.MaxValue], [Lod("busy" + o, objects[o])]));
        return mesh;
    }

    private static PegTextureSet Packs(IEnumerable<Tex> textures)
    {
        var list = textures.Select((t, i) => (t.Name, t.Size, t.Size, 0xFF000000u | (uint)(40 + i * 20) << 16 | (uint)(200 - i * 20))).ToArray();
        return new PegTextureSet("test", [new PegPack("busy_vcm.peg", () => SyntheticRf2Mesh.SamplePeg(list))]);
    }

    private static LegacyMeshConversion Convert(SynthRf2 mesh, IEnumerable<Tex> textures, Rf2TextureMode mode = Rf2TextureMode.Translate) =>
        LegacyMeshSupport.Convert(mesh.Build(), "busy.rfc", options: new LegacyConvertOptions { Rf2Textures = mode, TexturePacks = Packs(textures) });

    private static V3dSubmesh Single(LegacyMeshConversion conversion) => V3dReader.Read(conversion.Bytes, conversion.OutputName).Submeshes.Single();

    private static IEnumerable<string> Names(V3dSubmesh sub) => sub.Lods[0].Textures.Select(t => t.FileName);

    [Fact]
    public void NineTextures_ConvertToOneSubmeshWithAnAtlasOfThree()
    {
        var textures = Textures(9);
        var conversion = Convert(Character(textures), textures);
        var sub = Single(conversion);
        Assert.Equal(7, sub.Lods[0].Textures.Length);
        Assert.Equal(7, sub.Materials.Length);
        Assert.Equal(9, sub.Lods[0].Batches.Length);
        var atlas = Assert.Single(conversion.Textures);
        Assert.Equal("busy_atlas.tga", atlas.Name);
        Assert.Contains("busy_atlas.tga", Names(sub));
        Assert.Contains(conversion.Report, r => r.Contains("3 textures were combined into busy_atlas.tga", StringComparison.Ordinal));
        Assert.DoesNotContain(conversion.Report, r => r.Contains("split", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(MeshLinter.Analyze(conversion.Mesh, new MeshLintContext { FileName = conversion.OutputName }), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Conversion_KeepsTrianglesAndBoneWeights()
    {
        var textures = Textures(9);
        var sub = Single(Convert(Character(textures), textures));
        var batches = sub.Lods[0].Batches;
        Assert.Equal(9, batches.Sum(b => b.TriangleCount));
        Assert.All(batches, b => Assert.All(b.BoneLinks, l => Assert.Equal((255, 0), (l.GetWeight(0), l.GetBone(0)))));
    }

    [Fact]
    public void AtlasUvs_ShowTheSamePixelsAsTheSources()
    {
        var textures = Textures(9);
        var packs = Packs(textures);
        var conversion = LegacyMeshSupport.Convert(Character(textures).Build(), "busy.rfc", options: new LegacyConvertOptions { TexturePacks = packs });
        var read = LegacyMeshSupport.Read(Character(textures).Build(), "busy.rfc").Description.Submeshes.Single().Lods[0].Groups;
        var sourceMaterials = LegacyMeshSupport.Read(Character(textures).Build(), "busy.rfc").Description.Submeshes.Single().Materials;
        var atlas = ImageDecoder.Decode(conversion.Textures[0].Bytes, "busy_atlas.tga");
        var sub = Single(conversion);
        var batches = sub.Lods[0].Batches;
        int inAtlas = 0;
        for (int b = 0; b < batches.Length; b++)
        {
            if (sub.Lods[0].Textures[batches[b].TextureIndex].FileName != "busy_atlas.tga") continue;
            inAtlas++;
            var source = packs.Decode(sourceMaterials[read[b].Material].DiffuseMap.Text)!;
            for (int v = 0; v < batches[b].VertexCount; v++)
            {
                var from = read[b].Vertices[v].TexCoord;
                var to = batches[b].TexCoords[v];
                Assert.InRange(to.X, 0f, 1f);
                Assert.InRange(to.Y, 0f, 1f);
                Assert.Equal(Pixel(source, from.X * (source.Width - 1), from.Y * (source.Height - 1)), Pixel(atlas, to.X * atlas.Width, to.Y * atlas.Height));
            }
        }
        Assert.Equal(3, inAtlas);
    }

    private static uint Pixel(BgraImage image, float x, float y) =>
        BitConverter.ToUInt32(image.Pixels, ((int)y * image.Width + (int)x) * 4) | 0xFF000000u;

    [Fact]
    public void TilingTexture_IsNeverCombined()
    {
        var textures = Textures(9);
        textures[0] = textures[0] with { Uv = 2f };
        textures[1] = textures[1] with { Uv = 1.02f };
        var sub = Single(Convert(Character(textures), textures));
        Assert.Contains(textures[0].Name, Names(sub));
        Assert.Contains(textures[1].Name, Names(sub));
    }

    [Fact]
    public void Atlas_MixesNoMaterialSettings()
    {
        Tex[] textures =
        [
            new("a1.tga"), new("a2.tga"), new("b1.tga", Flags: 9), new("b2.tga", Flags: 9), new("c1.tga", Environment: "Chrome.tga"), new("c2.tga", Environment: "Chrome.tga"),
            new("d1.tga"), new("e1.tga", Flags: 9), new("f1.tga", Environment: "Chrome.tga"),
        ];
        var conversion = Convert(Character(textures), textures, Rf2TextureMode.KeepOriginals);
        var atlases = conversion.Report.Where(r => r.Contains("combined into", StringComparison.Ordinal)).ToList();
        Assert.Single(atlases);
        int kinds = new[] { "a1", "a2", "d1" }.Any(atlases[0].Contains) ? 1 : 0;
        kinds += new[] { "b1", "b2", "e1" }.Any(atlases[0].Contains) ? 1 : 0;
        kinds += new[] { "c1", "c2", "f1" }.Any(atlases[0].Contains) ? 1 : 0;
        Assert.Equal(1, kinds);
    }

    [Fact]
    public void Atlas_PrefersTheSmallestTexturesOfOneFamily()
    {
        var textures = new List<Tex>();
        for (int i = 1; i <= 6; i++) textures.Add(new Tex($"gun{i}.tga", Size: 64));
        for (int i = 1; i <= 3; i++) textures.Add(new Tex($"arm{i}.tga", Size: 32));
        var conversion = Convert(Character(textures), textures);
        Assert.Equal(["gun1.tga", "gun2.tga", "gun3.tga", "gun4.tga", "gun5.tga", "gun6.tga", "busy_atlas.tga"], Names(Single(conversion)));
        var atlas = ImageDecoder.Decode(conversion.Textures[0].Bytes, "busy_atlas.tga");
        Assert.Equal((64, 64), (atlas.Width, atlas.Height));
    }

    [Fact]
    public void DifferentSizes_KeepTheirPixelsInEqualCells()
    {
        Tex[] small = [new("s1.tga", Size: 8), new("s2.tga", Size: 32), new("s3.tga", Size: 16)];
        Tex[] textures = [.. small, .. Enumerable.Range(0, 6).Select(i => new Tex($"big{i}.tga", Uv: 2f))];
        var conversion = Convert(Character(textures), textures);
        var atlas = ImageDecoder.Decode(conversion.Textures[0].Bytes, "busy_atlas.tga");
        Assert.Equal((64, 64), (atlas.Width, atlas.Height));
        var first = Single(conversion).Lods[0].Batches[0].TexCoords;
        Assert.InRange(first.Max(uv => uv.X), 0f, 8f / 64f);
    }

    [Fact]
    public void AnAtlasNeverExceedsTheLimitSide()
    {
        Tex[] textures = [.. Enumerable.Range(0, 9).Select(i => new Tex($"huge{i}.tga", Size: 512))];
        var conversion = Convert(Character(textures), textures);
        var atlas = ImageDecoder.Decode(conversion.Textures[0].Bytes, "busy_atlas.tga");
        Assert.True(atlas.Width <= V3dTextureAtlas.MaxSize && atlas.Height <= V3dTextureAtlas.MaxSize, $"{atlas.Width} x {atlas.Height}");
    }

    [Fact]
    public void TooManyTilingTextures_StopWithTheNamesAndTheLimit()
    {
        Tex[] textures = [.. Enumerable.Range(0, 9).Select(i => new Tex($"tile{i}.tga", Uv: 3f))];
        var ex = Assert.Throws<AssetFormatException>(() => Convert(Character(textures), textures));
        Assert.Contains("tile0.tga (it tiles)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("holds 7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingImages_StopTheConversionButNotThePreview()
    {
        var textures = Textures(9);
        var bytes = Character(textures).Build();
        var ex = Assert.Throws<AssetFormatException>(() => LegacyMeshSupport.Convert(bytes, "busy.rfc"));
        Assert.Contains("texa.tga (its image was not found)", ex.Message, StringComparison.Ordinal);
        var (mesh, compiled) = LegacyMeshSupport.ReadCompiled(bytes, "busy.rfc");
        Assert.Equal(9, compiled.Submeshes.Single().Materials.Length);
        Assert.Contains(mesh.Notes, n => n.Contains("atlas", StringComparison.Ordinal));
    }

    [Fact]
    public void AtlasName_FitsATextureName()
    {
        var textures = Textures(9);
        string longName = new string('n', 40) + ".rfc";
        var conversion = LegacyMeshSupport.Convert(Character(textures).Build(), longName, options: new LegacyConvertOptions { TexturePacks = Packs(textures) });
        Assert.True(conversion.Textures[0].Name.Length <= V3dMaterial.NameSize - 1, conversion.Textures[0].Name);
        Assert.EndsWith("_atlas.tga", conversion.Textures[0].Name, StringComparison.Ordinal);
    }

    [Fact]
    public void KeptNames_WriteTheAtlasAndTheOtherTexturesOnly()
    {
        var textures = Textures(9);
        var conversion = Convert(Character(textures), textures, Rf2TextureMode.KeepOriginals);
        Assert.Equal(7, conversion.Textures.Length);
        Assert.Contains(conversion.Textures, t => t.Name == "busy_atlas.tga");
    }

    [Fact]
    public void TwoObjects_BecomeOneSubmesh()
    {
        Tex[] first = [new("a.tga"), new("shared.tga")], second = [new("shared.tga"), new("b.tga")];
        Tex[] all = [.. first, second[1]];
        var conversion = Convert(Character(first, second), all, Rf2TextureMode.KeepOriginals);
        var sub = Single(conversion);
        Assert.Equal("busy0", sub.Name.Text);
        Assert.Equal(["a.tga", "shared.tga", "b.tga"], sub.Materials.Select(m => m.DiffuseMap.Text));
        Assert.Equal(4, sub.Lods[0].Batches.Length);
        Assert.Equal(4, sub.Lods[0].Batches.Sum(b => b.TriangleCount));
        Assert.Contains(conversion.Report, r => r.Contains("joined into one submesh", StringComparison.Ordinal));
        Assert.DoesNotContain(conversion.Textures, t => t.Name.Contains("atlas", StringComparison.Ordinal));
    }

    [Fact]
    public void ObjectsWithDifferentLodCounts_KeepTheShorterObjectsLastLod()
    {
        var mesh = new SynthRf2 { Skinned = true };
        mesh.Bones.Add(("root", -1, Identity, Vector3.Zero));
        mesh.Objects.Add(([20f, float.MaxValue], [Lod("near", [new Tex("a.tga")]), Lod("far", [new Tex("b.tga")])]));
        mesh.Objects.Add(([float.MaxValue], [Lod("always", [new Tex("c.tga")])]));
        var sub = Assert.Single(LegacyMeshSupport.Read(mesh.Build(), "busy.rfc").Description.Submeshes);
        Assert.Equal(2, sub.Lods.Length);
        Assert.Equal(20f, sub.Lods[1].Distance);
        Assert.All(sub.Lods, l => Assert.Equal(2, l.Groups.Length));
    }

    // ── Existing files: V3C028 and the merge ────────────────────────────────

    private static V3dMaterial Material(string name, uint flags = 1) =>
        new(FixedString.FromText(name, V3dMaterial.NameSize), 0, 0, 0, 0, FixedString.FromText("", V3dMaterial.NameSize), flags);

    private static V3dMaterialGroup Group(int material, float x)
    {
        var bone = ImmutableArray.Create(new V3dBoneInfluence(0, 1f));
        return new V3dMaterialGroup
        {
            Material = material,
            Vertices = [new(new Vector3(x, 0, 0), Vector3.UnitY, Vector2.Zero, bone), new(new Vector3(x, 0, 1), Vector3.UnitY, Vector2.UnitY, bone), new(new Vector3(x + 1, 0, 0), Vector3.UnitY, Vector2.UnitX, bone)],
            Triangles = [new V3dMeshTriangle(0, 1, 2)],
        };
    }

    private static V3dSubmeshDescription Part(string name, float x, params string[] textures) => new()
    {
        Name = FixedString.FromText(name, V3dSubmesh.NameSize),
        Materials = [.. textures.Select(t => Material(t))],
        Lods = [new V3dLodDescription { Groups = [.. textures.Select((_, i) => Group(i, x + i))] }],
    };

    private static V3dFile Mesh(V3dKind kind, params V3dSubmeshDescription[] parts) => V3dBuilder.Build(new V3dMeshDescription
    {
        Kind = kind,
        Submeshes = [.. parts],
        Bones = kind == V3dKind.Character ? [V3dBuilder.BoneFromRestWorld("root", -1, Cairn.Formats.Maths.Rigid.Identity)] : [],
        PropPoints = [new V3dPropPoint(FixedString.FromText("muzzle_1", V3dPropPoint.NameSize), Quaternion.Identity, Vector3.UnitX, -1)],
    }, V3dBuildOptions.Preserve);

    private static V3dFile TwoParts() => Mesh(V3dKind.Character, Part("a", 0, "one.tga", "two.tga"), Part("b", 10, "two.tga", "three.tga"));

    [Fact]
    public void V3C028_FiresOnACharacterWithTwoSubmeshes_NotOnAStaticMesh()
    {
        var d = MeshLinter.Analyze(TwoParts()).Single(x => x.Code == MeshRules.SeveralSubmeshes);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("keeps only the last submesh", d.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MeshLinter.Analyze(Mesh(V3dKind.StaticMesh, Part("a", 0, "one.tga"), Part("b", 10, "two.tga"))), x => x.Code == MeshRules.SeveralSubmeshes);
        Assert.DoesNotContain(MeshLinter.Analyze(Mesh(V3dKind.Character, Part("a", 0, "one.tga"))), x => x.Code == MeshRules.SeveralSubmeshes);
    }

    [Fact]
    public void V3C009_AdvisesSplittingOnlyStaticMeshes()
    {
        string[] many = [.. Enumerable.Range(0, 8).Select(i => $"t{i}.tga")];
        string Help(V3dKind kind) => MeshLinter.Analyze(Mesh(kind, Part("a", 0, many))).Single(x => x.Code == MeshRules.TooManyTextures).Help!;
        Assert.DoesNotContain("split", Help(V3dKind.Character), StringComparison.Ordinal);
        Assert.Contains("split", Help(V3dKind.StaticMesh), StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_RoundTripsAndLintsClean()
    {
        var mesh = TwoParts();
        var fix = MeshLinter.Analyze(mesh).Single(x => x.Code == MeshRules.SeveralSubmeshes).QuickFixes.Single();
        Assert.Equal(QuickFixKind.Edit, fix.Kind);
        var merged = fix.Apply(mesh);
        MeshEditTests.AssertValidEdit(mesh, merged);
        var sub = merged.Submeshes.Single();
        Assert.Equal(1, merged.Header.SubmeshCount);
        Assert.Equal(3, sub.Materials.Length);
        Assert.Equal(["one.tga", "two.tga", "three.tga"], Names(sub));
        Assert.Equal(4, sub.Lods[0].Batches.Length);
        Assert.Equal([0, 1, 1, 2], sub.Lods[0].Batches.Select(b => b.TextureIndex));
        var diagnostics = MeshLinter.Analyze(merged);
        Assert.DoesNotContain(diagnostics, x => x.Code is MeshRules.SeveralSubmeshes or MeshRules.MorphMapIndex or MeshRules.HeaderCounts);
        // the box grew to both parts, and the prop points were not doubled
        Assert.Equal(new Vector3(12, 0, 1), sub.AabbMax);
        Assert.Single(sub.Lods[0].PropPoints);
        Assert.Same(merged, fix.Apply(merged));
    }

    [Fact]
    public void Merge_SumsTheVertexCounts_AndEachBatchKeepsItsMorphRange()
    {
        var mesh = Mesh(V3dKind.Character, Part("a", 0, "one.tga"), Part("b", 10, "two.tga"));
        var (a, b) = (mesh.Submeshes.First().Lods[0], mesh.Submeshes.Last().Lods[0]);
        var lod = V3dBuilder.Build(V3dSubmeshMerge.Merge(mesh), V3dBuildOptions.Preserve).Submeshes.Single().Lods[0];
        Assert.Equal(a.VertexCount + b.VertexCount, lod.VertexCount);
        Assert.Equal([0, 1, 2, -1, -1, -1], lod.Batches[0].MorphMap.Select(x => (int)x));
        Assert.Equal([-1, -1, -1, 0, 1, 2], lod.Batches[1].MorphMap.Select(x => (int)x));
    }

    [Fact]
    public void Merge_OfACutCharacter_KeepsItsOneMorphList()
    {
        var nan = new Vector3(float.NaN);
        var p = new Vector3[] { new(0, 0, 0), new(0, 0, 1), new(1, 0, 0), new(10, 0, 0), new(10, 0, 1), new(11, 0, 0) };
        var a = Part("a", 0, "one.tga");
        var b = Part("b", 10, "two.tga");
        a = a with { Lods = [a.Lods[0] with { MorphVertices = [p[0], p[1], p[2], nan, nan, nan] }] };
        b = b with { Lods = [b.Lods[0] with { MorphVertices = [nan, nan, nan, p[3], p[4], p[5]] }] };
        var merged = V3dSubmeshMerge.Merge([a, b]);
        Assert.Equal(p, merged.Lods[0].MorphVertices);
    }

    [Fact]
    public void Merge_KeepsMaterialsThatDrawDifferently()
    {
        var a = Part("a", 0, "one.tga");
        var b = Part("b", 10, "one.tga");
        b = b with { Materials = [Material("one.tga", 9)] };
        Assert.Equal(2, V3dSubmeshMerge.Merge([a, b]).Materials.Length);
    }

    [Fact]
    public void Merge_RefusesDifferentLodCountsAndOffsets()
    {
        var one = Part("a", 0, "one.tga");
        var two = one with { Lods = [.. one.Lods, one.Lods[0] with { Distance = 10f }] };
        var mesh = Mesh(V3dKind.Character, one, two);
        var d = MeshLinter.Analyze(mesh).Single(x => x.Code == MeshRules.SeveralSubmeshes);
        Assert.Empty(d.QuickFixes);
        Assert.Contains("levels of detail", d.Help, StringComparison.Ordinal);
        Assert.Contains("offsets", V3dSubmeshMerge.Obstacle([one, one with { Offset = Vector3.UnitX }]), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => V3dSubmeshMerge.Merge([one, two]));
    }

    [Fact]
    public void Merge_OverTheTextureLimit_OffersTheAtlasFix()
    {
        string[] seven = [.. Enumerable.Range(0, 7).Select(i => $"t{i}.tga")];
        var mesh = Mesh(V3dKind.Character, Part("a", 0, seven), Part("b", 10, "x.tga", "y.tga"));
        var fix = MeshLinter.Analyze(mesh).Single(x => x.Code == MeshRules.SeveralSubmeshes).QuickFixes.Single();
        Assert.Equal(QuickFixKind.MergeWithAtlas, fix.Kind);

        var merged = V3dSubmeshMerge.Merge(mesh);
        var image = new BgraImage(4, 4);
        var atlas = V3dTextureAtlas.Combine(merged.Submeshes[0], "busy", _ => image);
        var file = V3dBuilder.Build(merged with { Submeshes = [atlas.Submesh] }, V3dBuildOptions.Preserve);
        var sub = file.Submeshes.Single();
        Assert.Equal(7, sub.Lods[0].Textures.Length);
        Assert.Equal(9, sub.Lods[0].Batches.Length);
        Assert.Equal(3, atlas.Combined.Single().Sources.Length);
    }

    [Fact]
    public void Atlas_LeavesASubmeshWithinTheLimitAlone()
    {
        var part = Part("a", 0, "one.tga", "two.tga");
        var result = V3dTextureAtlas.Combine(part, "busy", _ => throw new InvalidOperationException("no pixels are needed"));
        Assert.Same(part, result.Submesh);
        Assert.Empty(result.Files);
    }
}
