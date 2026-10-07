using System.Numerics;
using Cairn.Rfa.Linting;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Linting;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

public sealed class VfxTransplantTests(ITestOutputHelper output)
{
    private static (string Name, VfxFile File)[]? Stock() =>
        LocalPaths.Corpus is { } dir && Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.vfx").Order(StringComparer.OrdinalIgnoreCase)
                .Select(f => (Path.GetFileName(f), VfxReader.Read(File.ReadAllBytes(f), Path.GetFileName(f)))).ToArray()
            : null;

    /// <summary>root (dummy), box (mesh, child of root), wind (spacewarp), sparks (particles, child of root, warp wind), glow (light, child of box), one material.</summary>
    private static VfxFile Synthetic()
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("fx.tga"));
        f = VfxEdit.AddSection(f, VfxBuilder.Dummy("root", 2));
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("box") with { Parent = "root" });
        f = VfxEdit.AddSection(f, VfxBuilder.Spacewarp("wind", 0, 2));
        f = VfxEdit.AddSection(f, VfxBuilder.ParticleSystem("sparks", 0, 2, "root") with { Warps = ["wind"] });
        f = VfxEdit.AddSection(f, VfxBuilder.Light("glow", 2, "box"));
        return f;
    }

    private static VfxFile ReRead(VfxFile f) => VfxReader.Read(VfxWriter.Write(f), "out.vfx");

    private static int Objects(VfxFile f) => f.Sections.Count(VfxTransplant.IsObject);

    private static int Materials(VfxFile f) => f.Sections.OfType<VfxMaterial>().Count();

    private static void CheckTransplant(string label, VfxFile source, VfxFile target, VfxTransplantResult r)
    {
        var up = source.Version == VfxVersion.Current ? source : VfxUpgrade.ToCurrent(source);
        var file = r.File;
        Assert.Equal(VfxVersion.Current, file.Version);
        Assert.Equal(Objects(target) + Objects(source), Objects(file));
        Assert.Equal(Materials(target) + r.AddedMaterials, Materials(file));
        Assert.Equal(Objects(source), r.Sections.Count);

        var names = file.Sections.Select(VfxEdit.NameOf).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetNames = target.Sections.Select(VfxEdit.NameOf).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fileMats = file.Sections.OfType<VfxMaterial>().ToArray();
        var srcMats = up.Sections.OfType<VfxMaterial>().ToArray();
        foreach (var c in r.Sections)
        {
            var s = file.Sections[c.TargetIndex];
            Assert.Equal(c.TargetName, VfxEdit.NameOf(s));
            Assert.DoesNotContain(c.TargetName, targetNames);
            string parent = s switch { VfxMesh m => m.Parent, VfxParticleSystem p => p.Parent, VfxDummy d => d.Parent, VfxLight l => l.Parent, VfxSpacewarp w => w.Parent, _ => "" };
            Assert.True(parent == "Scene Root" || names.Contains(parent), $"{label}: '{c.TargetName}' parent '{parent}' does not resolve");
            var orig = up.Sections[c.SourceIndex];
            if (s is VfxParticleSystem ps)
            {
                Assert.All(ps.Warps, w => Assert.Contains(w, names));
                if (ps.MaterialIndex is int pm && pm >= 0) Assert.True(VfxTransplant.SameMaterial(srcMats[((VfxParticleSystem)orig).MaterialIndex!.Value], fileMats[pm]));
            }
            if (s is VfxMesh { MaterialIndices: { } mi })
            {
                var omi = ((VfxMesh)orig).MaterialIndices!.Value;
                for (int k = 0; k < mi.Length; k++)
                    if (omi[k] >= 0) Assert.True(VfxTransplant.SameMaterial(srcMats[omi[k]], fileMats[mi[k]]), $"{label}: '{c.TargetName}' slot {k}");
            }
        }

        var back = ReRead(file);
        Assert.Equal(file.Sections.Length, back.Sections.Length);
        var errors = VfxLinter.Lint(back).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.True(errors.Count == 0, $"{label}: {string.Join("; ", errors)}");
    }

    [Fact]
    public void EveryStockEffectTransplantsIntoStockAndEmptyFiles()
    {
        if (Stock() is not { } stock) return;
        var target = stock.First(s => s.File.Version == VfxVersion.Current && Materials(s.File) > 0 && Objects(s.File) > 0);
        var targetBytes = VfxWriter.Write(target.File);
        int copied = 0, added = 0, reused = 0, warnings = 0;
        foreach (var (name, src) in stock)
        {
            var before = VfxWriter.Write(src);
            var intoStock = VfxTransplant.CopyAll(src, target.File);
            CheckTransplant($"{name} -> {target.Name}", src, target.File, intoStock);
            (copied, added, reused, warnings) = (copied + intoStock.Sections.Count, added + intoStock.AddedMaterials,
                reused + intoStock.MaterialMap.Count - intoStock.AddedMaterials, warnings + intoStock.Warnings.Count);
            var intoEmpty = VfxTransplant.CopyAll(src, VfxBuilder.NewFile(), timeOffset: 5);
            CheckTransplant($"{name} -> empty", src, VfxBuilder.NewFile(), intoEmpty);
            Assert.True(intoEmpty.File.EndFrame >= 5 || Objects(src) == 0);
            Assert.Equal(before, VfxWriter.Write(src));
        }
        Assert.Equal(targetBytes, VfxWriter.Write(target.File));

        // An older target is upgraded first and says so.
        var old = stock.First(s => s.File.Version < VfxVersion.Current);
        var intoOld = VfxTransplant.CopyAll(target.File, old.File);
        Assert.StartsWith("Target upgraded", intoOld.Warnings[0]);
        CheckTransplant($"{target.Name} -> {old.Name}", target.File, VfxUpgrade.ToCurrent(old.File), intoOld);
        output.WriteLine($"{stock.Length} files -> {target.Name}: {copied} sections, {added} materials appended, {reused} reused (merged), {warnings} warnings");
    }

    [Fact]
    public void DuplicateKeepsReferencesInsideTheCopiedSet()
    {
        var f = Synthetic();
        var before = VfxWriter.Write(f);
        int root = VfxEdit.FindByName(f, "root"), box = VfxEdit.FindByName(f, "box"), wind = VfxEdit.FindByName(f, "wind"), sparks = VfxEdit.FindByName(f, "sparks");
        var r = VfxTransplant.Duplicate(f, root, wind, sparks);
        Assert.Equal(before, VfxWriter.Write(f));
        Assert.Empty(r.Warnings);
        Assert.Equal(0, r.AddedMaterials);
        Assert.Equal(Materials(f), Materials(r.File));
        Assert.Equal(Objects(f) + 3, Objects(r.File));
        Assert.Equal("root_2", r.Names["root"]);
        var p = (VfxParticleSystem)r.File.Sections[VfxEdit.FindByName(r.File, "sparks_2")];
        Assert.Equal("root_2", p.Parent);
        Assert.Equal(new[] { "wind_2" }, p.Warps.ToArray());
        Assert.Equal(0, p.MaterialIndex);

        // A reference leaving the copied set stays when the file has the section.
        var m = VfxTransplant.Duplicate(f, box);
        var copy = (VfxMesh)m.File.Sections[m.Sections[0].TargetIndex];
        Assert.Equal("box_2", copy.Name);
        Assert.Equal("root", copy.Parent);
        Assert.Equal(((VfxMesh)f.Sections[box]).MaterialIndices!.Value.ToArray(), copy.MaterialIndices!.Value.ToArray());
        Assert.DoesNotContain(VfxLinter.Lint(ReRead(r.File)), d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(VfxLinter.Lint(ReRead(m.File)), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void MissingReferencesAreClearedAndTimeIsShifted()
    {
        var f = Synthetic();
        int sparks = VfxEdit.FindByName(f, "sparks"), root = VfxEdit.FindByName(f, "root"), box = VfxEdit.FindByName(f, "box");
        var r = VfxTransplant.Copy(f, VfxBuilder.NewFile(), [sparks], timeOffset: 3);
        var p = (VfxParticleSystem)r.File.Sections[r.Sections[0].TargetIndex];
        Assert.Equal("Scene Root", p.Parent);
        Assert.Empty(p.Warps);
        Assert.Equal(2, r.Warnings.Count);
        Assert.Equal(1, r.AddedMaterials);
        Assert.Equal(((VfxParticleSystem)f.Sections[sparks]).StartTime + 3, p.StartTime);

        var t = VfxTransplant.Copy(f, VfxBuilder.NewFile(), [root, box], timeOffset: 3);
        var d = (VfxDummy)t.File.Sections[t.Sections[0].TargetIndex];
        var mesh = (VfxMesh)t.File.Sections[t.Sections[1].TargetIndex];
        Assert.Equal(5, d.Frames.Length);
        Assert.Equal("root", mesh.Parent);
        Assert.Equal(((VfxMesh)f.Sections[box]).StartTime!.Value + 3f / 15, mesh.StartTime!.Value, 5);

        // Copying the same material twice into a file reuses it.
        var again = VfxTransplant.Copy(f, r.File, [sparks]);
        Assert.Equal(0, again.AddedMaterials);
        Assert.Equal("sparks_2", again.Sections[0].TargetName);
        Assert.Throws<ArgumentException>(() => VfxTransplant.Copy(f, r.File, [f.Sections.Length - 1]));
    }

    [Fact]
    public void SettersChangeOnlyWhatTheyName()
    {
        var f = Synthetic();
        int box = VfxEdit.FindByName(f, "box"), sparks = VfxEdit.FindByName(f, "sparks"), root = VfxEdit.FindByName(f, "root"),
            glow = VfxEdit.FindByName(f, "glow"), wind = VfxEdit.FindByName(f, "wind");

        var g = VfxEdit.SetFlag(f, box, "fullbright", true);
        Assert.NotEqual(0u, ((VfxMesh)g.Sections[box]).Flags & VfxMeshFlags.Fullbright);
        Assert.Equal(VfxWriter.Write(f), VfxWriter.Write(VfxEdit.SetFlag(g, box, "Fullbright", false)));
        Assert.True(((VfxParticleSystem)VfxEdit.SetFlag(f, sparks, "drops", true).Sections[sparks]).IsDrops);
        Assert.Throws<ArgumentException>(() => VfxEdit.SetFlag(f, sparks, "nope", true));

        var b = VfxEdit.SetObjectValue(f, sparks, VfxObjectValue.BirthRate, 7);
        Assert.All(((VfxParticleSystem)b.Sections[sparks]).Frames, x => Assert.Equal(7, x.BirthRate));
        var w = VfxEdit.SetObjectValue(f, wind, VfxObjectValue.Strength, 2, frame: 1);
        Assert.Equal(2, ((VfxSpacewarp)w.Sections[wind]).Frames[1].Strength);
        Assert.NotEqual(2, ((VfxSpacewarp)w.Sections[wind]).Frames[0].Strength);

        var l = VfxEdit.SetLightColor(f, glow, new Vector3(1, 0, 0));
        var light = (VfxLight)l.Sections[glow];
        Assert.Equal(new Vector3(1, 0, 0), light.Initial.Color);
        Assert.All(light.Frames, x => Assert.Equal(new Vector3(1, 0, 0), x.Color));
        Assert.Equal(0, ((VfxLight)VfxEdit.SetLightOn(f, glow, false, 0).Sections[glow]).Frames[0].IsOn);

        var d = (VfxDummy)VfxEdit.SetObjectPosition(f, root, Vector3.UnitY, frame: 1).Sections[root];
        Assert.Equal(Vector3.UnitY, d.Frames[1].Position);
        Assert.NotEqual(Vector3.UnitY, d.Position);
        Assert.Same(f, VfxEdit.SetObjectValue(f, sparks, VfxObjectValue.Speed, ((VfxParticleSystem)f.Sections[sparks]).Frames[0].Speed, 0));
        RoundTripsTo(VfxEdit.SetObjectOrientation(b, wind, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1)));
    }

    private static void RoundTripsTo(VfxFile f) => Assert.Equal(VfxWriter.Write(f), VfxWriter.Write(ReRead(f)));
}
