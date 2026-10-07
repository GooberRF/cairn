using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Workspace;

namespace Cairn.Vfx.Tests;

public sealed class VfxEditTests
{
    private static VfxFile[]? Stock() =>
        LocalPaths.Corpus is { } dir && Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.vfx").Order(StringComparer.OrdinalIgnoreCase).Select(f => VfxReader.Read(File.ReadAllBytes(f), Path.GetFileName(f))).ToArray()
            : null;

    private static byte[] RoundTrip(VfxFile f)
    {
        var bytes = VfxWriter.Write(f);
        Assert.Equal(bytes, VfxWriter.Write(VfxReader.Read(bytes, "test.vfx")));
        return bytes;
    }

    private static VfxMeshBuilder Quad() => new()
    {
        Name = "Quad",
        Frames = [[new(-1, 0, -1), new(1, 0, -1), new(1, 0, 1), new(-1, 0, 1)]],
        Triangles = [(0, 2, 1), (0, 3, 2)],
        Uvs = [new(0, 0), new(1, 1), new(1, 0), new(0, 0), new(0, 1), new(1, 1)],
        MaterialIndices = [0],
        FrameCount = 4,
    };

    private static VfxFile Sample()
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("fx.tga"));
        f = VfxEdit.AddSection(f, VfxBuilder.ColorMaterial());
        f = VfxEdit.AddSection(f, Quad().Build());
        var morph = Quad();
        morph.Name = "Morph";
        morph.Frames.Add(morph.Frames[0].Select(p => p * 2).ToArray());
        morph.Flags = VfxMeshFlags.Facing;
        f = VfxEdit.AddSection(f, morph.Build());
        var rod = Quad();
        rod.Name = "Rod";
        rod.Flags = VfxMeshFlags.FacingRod;
        rod.Keys = new VfxKeyLists([new(0, Vector3.Zero, Vector3.Zero, Vector3.Zero)], [new(0, Quaternion.Identity, 0, 0, 0, 0, 0)], []);
        f = VfxEdit.AddSection(f, rod.Build());
        f = VfxEdit.AddSection(f, VfxBuilder.ParticleSystem("Sparks", 1, 4));
        f = VfxEdit.AddSection(f, VfxBuilder.Dummy("Dummy", 4));
        f = VfxEdit.AddSection(f, VfxBuilder.Light("Light", 4));
        f = VfxEdit.AddSection(f, VfxBuilder.Spacewarp("Warp", 0, 4));
        return VfxEdit.RecomputeEndFrame(f);
    }

    [Fact]
    public void AdjacencyIsDerivableOnEveryStockMesh()
    {
        if (Stock() is not { } files) return;
        int meshes = 0;
        foreach (var m in files.Where(f => f.Version == VfxVersion.Current).SelectMany(f => f.Sections.OfType<VfxMesh>()))
        {
            var adj = VfxGeometry.Adjacency(m.Faces, m.FaceVertices.Length);
            for (int i = 0; i < adj.Length; i++) Assert.Equal(m.FaceVertices[i].AdjacentFaces.ToArray(), adj[i].ToArray());
            meshes++;
        }
        Assert.True(meshes > 100);
    }

    [Fact]
    public void FaceShapesAndFrameCountsMatchStock()
    {
        if (Stock() is not { } files) return;
        int faces = 0, normals = 0, centers = 0;
        foreach (var m in files.Where(f => f.Version == VfxVersion.Current).SelectMany(f => f.Sections.OfType<VfxMesh>()))
        {
            Assert.Equal(m.Frames.Length, VfxGeometry.FrameCount(m.StartTime!.Value, m.EndTime!.Value, m.Fps!.Value));
            var d = VfxGeometry.RefreshShape(m);
            for (int i = 0; i < m.Faces.Length; i++, faces++)
            {
                if (Vector3.Dot(d.Faces[i].Normal, m.Faces[i].Normal) > 0.999f) normals++;
                if (Vector3.Distance(d.Faces[i].Center, m.Faces[i].Center) < 1e-3f) centers++;
            }
        }
        Assert.True(normals > faces * 0.9, $"{normals}/{faces} normals");
        Assert.True(centers > faces * 0.9, $"{centers}/{faces} centres");
    }

    [Fact]
    public void BuilderOutputWritesAndRereads()
    {
        var f = Sample();
        RoundTrip(f);
        var m = (VfxMesh)f.Sections[VfxEdit.FindByName(f, "Morph")];
        Assert.True(m.IsMorph);
        Assert.Equal(2, m.Frames.Length);
        Assert.Equal(2, VfxGeometry.FrameCount(m.StartTime!.Value, m.EndTime!.Value, m.Fps!.Value));
        Assert.Equal(4, m.FaceVertices.Length);
        Assert.Equal(6, m.FaceVertices.Sum(r => r.AdjacentFaces.Length));
        Assert.Equal(3, f.EndFrame);
        Assert.All(m.FaceVertices, r => Assert.Equal(VfxGeometry.UnsetUv, r.RawU));
    }

    [Fact]
    public void PrimitivesWrite()
    {
        var f = VfxEdit.AddSection(VfxBuilder.NewFile(), VfxBuilder.ImageMaterial("a.tga"));
        foreach (var p in VfxPrimitives.All()) f = VfxEdit.AddSection(f, p);
        RoundTrip(f);
    }

    [Fact]
    public void PrimitivesLintClean()
    {
        foreach (var p in VfxPrimitives.All())
        {
            var f = VfxEdit.AddSection(VfxEdit.AddSection(VfxBuilder.NewFile(), VfxBuilder.ImageMaterial("a.tga")), p);
            var found = Cairn.Vfx.Linting.VfxLinter.Lint(f)
                .Where(d => d.Severity is Cairn.Rfa.Linting.DiagnosticSeverity.Warning or Cairn.Rfa.Linting.DiagnosticSeverity.Error
                    || d.Code == Cairn.Vfx.Linting.VfxRules.ZeroAreaFace)
                .Select(d => $"{p.Name}: {d.Code} {d.Message}").ToList();
            Assert.Empty(found);
        }
    }

    [Fact]
    public void EditsRequireCurrentVersion()
    {
        var old = VfxBuilder.NewFile() with { Version = 0x3000F };
        Assert.Throws<InvalidOperationException>(() => VfxEdit.SetEndFrame(old, 3));
    }

    private static void Undo(VfxFile f, Func<VfxFile, VfxFile> edit, Func<VfxFile, VfxFile> inverse)
    {
        var before = VfxWriter.Write(f);
        var edited = edit(f);
        Assert.NotSame(f, edited);
        RoundTrip(edited);
        Assert.Equal(before, VfxWriter.Write(inverse(edited)));
    }

    [Fact]
    public void InverseEditsRestoreBytes()
    {
        var f = Sample();
        int quad = VfxEdit.FindByName(f, "Quad"), morph = VfxEdit.FindByName(f, "Morph"), rod = VfxEdit.FindByName(f, "Rod");
        int mat0 = VfxEdit.MaterialSectionIndex(f, 0);
        Undo(f, x => VfxEdit.Rename(x, quad, "Other"), x => VfxEdit.Rename(x, quad, "Quad"));
        Undo(f, x => VfxEdit.Reparent(x, quad, "Rod"), x => VfxEdit.Reparent(x, quad, "Scene Root"));
        Undo(f, x => VfxEdit.DuplicateSection(x, quad), x => VfxEdit.RemoveSection(x, quad + 1));
        Undo(f, x => VfxEdit.MoveSection(x, mat0, mat0 + 1), x => VfxEdit.MoveSection(x, mat0 + 1, mat0));
        Undo(f, x => VfxEdit.FlipWinding(x, quad), x => VfxEdit.FlipWinding(x, quad));
        Undo(f, x => VfxEdit.SetFaceMaterial(x, quad, -1, [1]), x => VfxEdit.SetFaceMaterial(x, quad, 0, [1]));
        Undo(f, x => VfxEdit.SetFaceSmoothing(x, quad, 2, [1]), x => VfxEdit.SetFaceSmoothing(x, quad, 1, [1]));
        Undo(f, x => VfxEdit.SetFrameCount(x, quad, 9, VfxFrameFill.Loop), x => VfxEdit.SetFrameCount(x, quad, 4));
        Undo(f, x => VfxEdit.SetFrameCount(x, morph, 5, VfxFrameFill.Resample), x => VfxEdit.SetFrameCount(x, morph, 2, VfxFrameFill.Resample));
        Undo(f, x => VfxEdit.AddMorphFrame(x, morph, 1), x => VfxEdit.RemoveMorphFrame(x, morph, 1));
        Undo(f, x => VfxEdit.ToKeyframes(x, quad, reduce: false), x => VfxEdit.ToPerFrameTransforms(x, quad));
        Undo(f, x => VfxEdit.SetMeshFlags(x, quad, VfxMeshFlags.DumpUvs | VfxMeshFlags.Fullbright), x => VfxEdit.SetMeshFlags(x, quad, 0));
        Undo(f, x => VfxEdit.SetFps(x, quad, 30), x => VfxEdit.SetFps(x, quad, 15));
        Undo(f, x => VfxEdit.SetFacingSize(x, morph, new(2, 3), 1), x => VfxEdit.SetFacingSize(x, morph, Vector2.One, 1));
        Undo(f, x => VfxEdit.SetUpVector(x, rod, Vector3.UnitX), x => VfxEdit.SetUpVector(x, rod, Vector3.UnitY));
        Undo(f, x => VfxEdit.SetKey(x, rod, VfxKeyChannel.Translation, new(640, Vector3.One, Vector3.Zero, Vector3.Zero)),
            x => VfxEdit.DeleteKey(x, rod, VfxKeyChannel.Translation, 640));
        Undo(f, x => VfxEdit.MoveKey(x, rod, VfxKeyChannel.Rotation, 0, 320), x => VfxEdit.MoveKey(x, rod, VfxKeyChannel.Rotation, 320, 0));
        Undo(f, x => VfxEdit.SetStaticTransform(x, quad, VfxBuilder.Identity with { Scale = new(2) }, 2),
            x => VfxEdit.SetStaticTransform(x, quad, VfxBuilder.Identity, 2));
        Undo(f, x => VfxEdit.ResizeTrack(x, mat0, VfxMaterialTrack.Opacity, 10), x => VfxEdit.ResizeTrack(x, mat0, VfxMaterialTrack.Opacity, 1));
        Undo(f, x => VfxEdit.SetTexture(x, mat0, "b.tga"), x => VfxEdit.SetTexture(x, mat0, "fx.tga"));
        int ps = VfxEdit.FindByName(f, "Sparks"), light = VfxEdit.FindByName(f, "Light");
        Undo(f, x => VfxEdit.ResizeObjectFrames(x, ps, 8), x => VfxEdit.ResizeObjectFrames(x, ps, 4));
        Undo(f, x => VfxEdit.SetObjectFrame<VfxLightParams>(x, light, l => l with { Radius = 3 }, 2),
            x => VfxEdit.SetObjectFrame<VfxLightParams>(x, light, l => l with { Radius = 10 }, 2));
        Undo(f, x => VfxEdit.Update<VfxSpacewarp>(x, VfxEdit.FindByName(f, "Warp"), w => w with { Type = 1 }),
            x => VfxEdit.Update<VfxSpacewarp>(x, VfxEdit.FindByName(f, "Warp"), w => w with { Type = 0 }));
        Assert.Same(f, VfxEdit.SetFps(f, quad, 15));
        Assert.Same(f, VfxEdit.Rename(f, quad, "Quad"));
    }

    [Fact]
    public void MaterialTypeChangesAddAndDropTheirFields()
    {
        var f = Sample();
        int mat = VfxEdit.MaterialSectionIndex(f, 0);
        foreach (int type in new[] { 2, 1, 0, 1, 2, 0 })
        {
            f = VfxEdit.SetMaterialType(f, mat, type);
            Assert.Equal(type, ((VfxMaterial)f.Sections[mat]).Type);
            RoundTrip(f);
        }
    }

    [Fact]
    public void DuplicatingAMaterialKeepsEveryMaterialReference()
    {
        var f = Sample();
        var mats = f.Sections.OfType<VfxMaterial>().ToArray();
        var users = f.Sections.OfType<VfxParticleSystem>().Select(p => mats[p.MaterialIndex!.Value]).ToArray();
        var g = VfxEdit.DuplicateSection(f, VfxEdit.MaterialSectionIndex(f, 0));
        var after = g.Sections.OfType<VfxMaterial>().ToArray();
        Assert.Equal(mats.Length + 1, after.Length);
        var ps = g.Sections.OfType<VfxParticleSystem>().ToArray();
        for (int i = 0; i < ps.Length; i++) Assert.Same(users[i], after[ps[i].MaterialIndex!.Value]);
        RoundTrip(g);
    }

    /// <summary>Two materials, both used by a mesh slot and a particle system; duplicating the first must not move the
    /// users of the second (an insert right after the original would shift them onto the copy).</summary>
    private static VfxFile TwoMaterials()
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("ta.tga"));
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("tb.tga"));
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("BoxB", null, 1));
        f = VfxEdit.AddSection(f, VfxBuilder.Dummy("Host", 4));
        f = VfxEdit.AddSection(f, VfxBuilder.ParticleSystem("PartB", 1, 4, "Host"));
        return VfxEdit.AddSection(f, VfxBuilder.ParticleSystem("PartA", 0, 4, "Host"));
    }

    [Fact]
    public void DuplicatingAMaterialKeepsTheUsersOfLaterMaterials()
    {
        var f = TwoMaterials();
        var g = VfxEdit.DuplicateSection(f, VfxEdit.MaterialSectionIndex(f, 0));
        var mats = g.Sections.OfType<VfxMaterial>().ToArray();
        Assert.Equal(3, mats.Length);
        string Tex(int m) => mats[m].Texture0!.Name;
        var box = (VfxMesh)g.Sections[VfxEdit.FindByName(g, "BoxB")];
        Assert.Equal("tb.tga", Tex(box.MaterialIndices!.Value[0]));
        Assert.Equal("tb.tga", Tex(((VfxParticleSystem)g.Sections[VfxEdit.FindByName(g, "PartB")]).MaterialIndex!.Value));
        Assert.Equal("ta.tga", Tex(((VfxParticleSystem)g.Sections[VfxEdit.FindByName(g, "PartA")]).MaterialIndex!.Value));
        RoundTrip(g);
    }

    [Fact]
    public void RemovingAMaterialNeverHandsParticlesMaterialZero()
    {
        var f = TwoMaterials();
        int matB = VfxEdit.MaterialSectionIndex(f, 1);
        // "none" for a material a particle system uses is refused with the reason, not turned into material 0
        var e = Assert.Throws<InvalidOperationException>(() => VfxEdit.RemoveSection(f, matB, reassignTo: -1));
        Assert.Contains("PartB", e.Message);
        // a mesh-only material may still go to "none"
        var noParticles = VfxEdit.RemoveSection(f, VfxEdit.FindByName(f, "PartB"));
        var g = VfxEdit.RemoveSection(noParticles, VfxEdit.MaterialSectionIndex(noParticles, 1), reassignTo: -1);
        Assert.Equal(0, ((VfxParticleSystem)g.Sections[VfxEdit.FindByName(g, "PartA")]).MaterialIndex);
        RoundTrip(g);
    }

    [Fact]
    public void OtherEditFamiliesProduceValidFiles()
    {
        var f = Sample();
        int quad = VfxEdit.FindByName(f, "Quad"), morph = VfxEdit.FindByName(f, "Morph"), mat0 = VfxEdit.MaterialSectionIndex(f, 0);
        Assert.Throws<InvalidOperationException>(() => VfxEdit.RemoveSection(f, mat0));
        var removed = VfxEdit.RemoveSection(f, mat0, reassignTo: 0);
        Assert.Equal(0, ((VfxParticleSystem)removed.Sections[VfxEdit.FindByName(removed, "Sparks")]).MaterialIndex);
        RoundTrip(removed);
        RoundTrip(VfxEdit.RemoveSection(f, mat0, reassignTo: -1));
        var g = VfxEdit.MoveVertices(f, morph, new Vector3(0, 1, 0), [0, 1]);
        g = VfxEdit.GenerateUvScroll(g, quad, new Vector2(0.25f, 0));
        g = VfxEdit.RotateUvs(g, quad, 0.5f, new Vector2(0.5f));
        g = VfxEdit.ToStatic(g, morph, 1);
        g = VfxEdit.ToMorph(g, quad);
        g = VfxEdit.ToKeyframes(g, VfxEdit.FindByName(g, "Morph"));
        g = VfxEdit.AutoTangents(g, VfxEdit.FindByName(g, "Rod"), VfxKeyChannel.Translation);
        g = VfxEdit.FillTrack(g, mat0, VfxMaterialTrack.Opacity, 15, [(0, 0f), (5, 1f), (14, 0f)]);
        g = VfxEdit.SetStartTime(g, quad, 1f);
        g = VfxEdit.SetFrameCount(g, quad, 2);
        RoundTrip(VfxEdit.RecomputeEndFrame(g));
        Assert.Equal(1f, ((VfxMaterial)g.Sections[mat0]).Opacity!.Value[5]);
    }

    [Fact]
    public void UpgradeConvertsEveryOlderStockFile()
    {
        if (Stock() is not { } files) return;
        var older = files.Where(f => f.Version != VfxVersion.Current).ToList();
        Assert.Equal(19, older.Count);
        foreach (var f in older)
        {
            var up = VfxUpgrade.ToCurrent(f);
            var back = VfxReader.Read(RoundTrip(up), "up.vfx");
            var a = f.Sections.OfType<VfxMesh>().ToList();
            var b = back.Sections.OfType<VfxMesh>().ToList();
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].NumVertices, b[i].NumVertices);
                Assert.Equal(a[i].Faces.Length, b[i].Faces.Length);
                Assert.Equal(a[i].Frames.Length, b[i].Frames.Length);
            }
            Assert.Equal(f.EndFrame, back.EndFrame); // the authored header end frame is kept (as REDUX), not recomputed
        }
    }
}
