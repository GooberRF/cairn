using Cairn.Rfa.Editing;
using Cairn.Formats;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Tests;

/// <summary>The mesh rules' pure quick fixes (phase 6), on synthetic meshes; stock meshes offer none on Error rules.</summary>
public class MeshFixTests
{
    private static V3dFile Sample() => MeshEditTests.Sample();

    private static V3dFile WithBone(V3dFile mesh, int index, Func<V3dBone, V3dBone> change)
    {
        int at = mesh.Sections.IndexOf(mesh.BoneSection!);
        return mesh with { Sections = mesh.Sections.SetItem(at, mesh.BoneSection! with { Bones = mesh.Bones.SetItem(index, change(mesh.Bones[index])) }) };
    }

    private static V3dFile WithSphere(V3dFile mesh, Func<V3dCollisionSphere, V3dCollisionSphere> change)
    {
        var sphere = mesh.CollisionSpheres.First();
        int at = mesh.Sections.IndexOf(sphere);
        return mesh with { Sections = mesh.Sections.SetItem(at, change(sphere)) };
    }

    private static V3dFile WithSubmesh(V3dFile mesh, Func<V3dSubmesh, V3dSubmesh> change)
    {
        var sub = mesh.Submeshes.First();
        int at = mesh.Sections.IndexOf(sub);
        return mesh with { Sections = mesh.Sections.SetItem(at, change(sub)) };
    }

    private static FixedString Full(int size, char c = 'x') => FixedString.FromBytes([.. Enumerable.Repeat((byte)c, size)]);

    /// <summary>The first <paramref name="code"/> diagnostic offers an edit fix that removes it and adds no error.</summary>
    private static V3dFile FixRemoves(string code, V3dFile broken)
    {
        var before = MeshLinter.Analyze(broken);
        var d = before.First(x => x.Code == code);
        var fix = d.QuickFixes.FirstOrDefault(f => f.Kind == QuickFixKind.Edit);
        Assert.NotNull(fix);
        var repaired = fix!.Apply(broken);
        Assert.NotSame(broken, repaired);
        var after = MeshLinter.Analyze(repaired);
        Assert.True(after.Count(x => x.Code == code) < before.Count(x => x.Code == code), $"{code} is still reported after '{fix.Title}'.");
        MeshEditTests.AssertValidEdit(broken, repaired);
        // A fix applied again (to the repaired mesh) has nothing left to do.
        Assert.Same(repaired, fix.Apply(repaired));
        return repaired;
    }

    [Fact]
    public void HeaderCountsFixRecomputesTheCounts()
    {
        var mesh = Sample();
        var broken = mesh with { Header = mesh.Header with { CollisionSphereCount = 5, SubmeshCount = 3 } };
        var repaired = FixRemoves(MeshRules.HeaderCounts, broken);
        Assert.Equal(mesh.Header, repaired.Header);
    }

    [Fact]
    public void BadParentFixMakesTheBoneARoot()
    {
        var broken = WithBone(Sample(), 1, b => b with { ParentIndex = 7 });
        var repaired = FixRemoves(MeshRules.BadParent, broken);
        Assert.Equal(-1, repaired.Bones[1].ParentIndex);
    }

    [Fact]
    public void DuplicateBoneNameFixAddsAUniqueSuffix()
    {
        var broken = MeshEdit.RenameBone(Sample(), 1, "PELVIS");
        var repaired = FixRemoves(MeshRules.DuplicateBoneName, broken);
        Assert.Equal("PELVIS_2", repaired.Bones[1].Name.Text);
        Assert.Equal("pelvis", repaired.Bones[0].Name.Text);

        // A full-length name is shortened so the suffix fits; taken suffixes are skipped.
        string longName = new('b', 23);
        var mesh = MeshEdit.RenameBone(MeshEdit.RenameBone(MeshEdit.RenameBone(Sample(), 0, longName), 1, longName), 2, new string('b', 21) + "_2");
        var fixedLong = FixRemoves(MeshRules.DuplicateBoneName, mesh);
        Assert.Equal(new string('b', 21) + "_3", fixedLong.Bones[1].Name.Text);
    }

    [Fact]
    public void NameTooLongFixesTruncateEveryKindOfName()
    {
        var mesh = Sample();
        var bone = FixRemoves(MeshRules.NameTooLong, WithBone(mesh, 0, b => b with { Name = Full(V3dBone.NameSize) }));
        Assert.Equal(new string('x', 23), bone.Bones[0].Name.Text);
        Assert.Equal(V3dBone.NameSize, bone.Bones[0].Name.Length);

        var sphere = FixRemoves(MeshRules.NameTooLong, WithSphere(mesh, s => s with { Name = Full(V3dCollisionSphere.NameSize) }));
        Assert.Equal(new string('x', 23), sphere.CollisionSpheres.First().Name.Text);

        var submesh = FixRemoves(MeshRules.NameTooLong, WithSubmesh(mesh, s => s with { Name = Full(V3dSubmesh.NameSize) }));
        Assert.Equal(new string('x', 23), submesh.Submeshes.First().Name.Text);

        var texture = FixRemoves(MeshRules.NameTooLong, WithSubmesh(mesh, s => s with { Materials = s.Materials.SetItem(0, s.Materials[0] with { DiffuseMap = Full(V3dMaterial.NameSize) }) }));
        Assert.Equal(new string('x', 31), texture.Submeshes.First().Materials[0].DiffuseMap.Text);

        var prop = FixRemoves(MeshRules.NameTooLong, WithSubmesh(mesh, s =>
            s with { Lods = s.Lods.SetItem(0, s.Lods[0] with { PropPoints = s.Lods[0].PropPoints.SetItem(0, s.Lods[0].PropPoints[0] with { Name = Full(V3dPropPoint.NameSize) }) }) }));
        Assert.Equal(new string('x', 67), prop.Submeshes.First().Lods[0].PropPoints[0].Name.Text);
        // The other LOD's prop point (which had a good name) is untouched.
        Assert.Equal(mesh.Submeshes.First().Lods[1].PropPoints[0], prop.Submeshes.First().Lods[1].PropPoints[0]);
    }

    [Fact]
    public void SphereFixesGiveARadiusAndAnExistingBone()
    {
        var radius = FixRemoves(MeshRules.SphereRadius, WithSphere(Sample(), s => s with { Radius = 0f }));
        Assert.Equal(MeshFixes.DefaultSphereRadius, radius.CollisionSpheres.First().Radius);
        var negative = FixRemoves(MeshRules.SphereRadius, WithSphere(Sample(), s => s with { Radius = -2f }));
        Assert.Equal(MeshFixes.DefaultSphereRadius, negative.CollisionSpheres.First().Radius);

        var bone = FixRemoves(MeshRules.SphereBone, WithSphere(Sample(), s => s with { BoneIndex = 9 }));
        Assert.Equal(2, bone.CollisionSpheres.First().BoneIndex); // bone 2 ('root') is the sample's root
    }

    [Fact]
    public void PropBoneFixAttachesOnlyThatLodsPropToTheRoot()
    {
        var mesh = Sample();
        var broken = WithSubmesh(mesh, s =>
            s with { Lods = s.Lods.SetItem(0, s.Lods[0] with { PropPoints = s.Lods[0].PropPoints.SetItem(0, s.Lods[0].PropPoints[0] with { ParentIndex = 12 }) }) });
        var repaired = FixRemoves(MeshRules.PropPointBone, broken);
        Assert.Equal(2, repaired.Submeshes.First().Lods[0].PropPoints[0].ParentIndex);
        Assert.Equal(1, repaired.Submeshes.First().Lods[1].PropPoints[0].ParentIndex);
    }

    [Fact]
    public void LodDistanceFixSortsAndSeparates()
    {
        var swapped = FixRemoves(MeshRules.LodDistances, WithSubmesh(Sample(), s => s with { LodDistances = [10f, 0f] }));
        Assert.Equal("0 10", string.Join(" ", swapped.Submeshes.First().LodDistances));
        var equal = FixRemoves(MeshRules.LodDistances, WithSubmesh(Sample(), s => s with { LodDistances = [5f, 5f] }));
        Assert.Equal(new[] { 5f, 5f + MeshFixes.LodDistanceGap }, equal.Submeshes.First().LodDistances.ToArray());
        Assert.Null(MeshFixes.Increasing([0f, 1f, 2f]));
        Assert.Equal([0f, 1f, 2f], MeshFixes.Increasing([2f, 0f, 1f]) ?? []);

        // A distance list that does not match the LOD count is not offered a fix.
        var mismatched = WithSubmesh(Sample(), s => s with { LodDistances = [10f, 0f, 3f] });
        var d = MeshLinter.Analyze(mismatched).First(x => x.Code == MeshRules.LodDistances);
        Assert.DoesNotContain(d.QuickFixes, f => f.Kind == QuickFixKind.Edit);
    }

    [Fact]
    public void RootBoneIsTheFirstBoneFkTreatsAsARoot()
    {
        Assert.Equal(2, MeshFixes.RootBone(Sample()));
        var noBones = Sample() with { Sections = [.. Sample().Sections.Where(s => s is not V3dBoneSection)] };
        Assert.Equal(-1, MeshFixes.RootBone(noBones));
        var sphereless = WithSphere(noBones, s => s with { BoneIndex = 3 });
        var repaired = FixRemoves(MeshRules.SphereBone, sphereless);
        Assert.Equal(-1, repaired.CollisionSpheres.First().BoneIndex);
    }

    [Fact]
    public void StockMeshesOfferNoEditFixForAnError()
    {
        string? path = TestPaths.CorpusFile("ult2_guard.v3c");
        if (path is null) return;
        var mesh = V3dReader.ReadFile(path);
        Assert.DoesNotContain(MeshLinter.Analyze(mesh), d => d.Severity == DiagnosticSeverity.Error);
        foreach (var d in MeshLinter.Analyze(mesh))
        {
            foreach (var fix in d.QuickFixes.Where(f => f.Kind == QuickFixKind.Edit))
                MeshEditTests.AssertNoNewErrors(mesh, fix.Apply(mesh));
        }
    }
}
