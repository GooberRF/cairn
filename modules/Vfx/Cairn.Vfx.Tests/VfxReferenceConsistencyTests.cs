using System.Linq;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Linting;
using Xunit;

namespace Cairn.Vfx.Tests;

public class VfxReferenceConsistencyTests
{
    private static VfxFile Scene()
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("fire.tga"));
        f = VfxEdit.AddSection(f, VfxBuilder.Dummy("root"));
        f = VfxEdit.AddSection(f, VfxBuilder.Spacewarp("wind", parent: "root"));
        var ps = VfxBuilder.ParticleSystem("sparks", 0, parent: "root");
        f = VfxEdit.AddSection(f, ps with { Warps = ["wind", "WIND", "other"] });
        f = VfxEdit.AddSection(f, VfxBuilder.Light("glow", parent: "sparks"));
        return f;
    }

    private static T Named<T>(VfxFile f, string name) where T : VfxSection => (T)f.Sections[VfxEdit.FindByName(f, name)];

    [Fact]
    public void RenameUpdatesParentsAndSpacewarpNames()
    {
        var f = Scene();
        f = VfxEdit.Rename(f, VfxEdit.FindByName(f, "root"), "base");
        Assert.Equal("base", Named<VfxSpacewarp>(f, "wind").Parent);
        Assert.Equal("base", Named<VfxParticleSystem>(f, "sparks").Parent);
        f = VfxEdit.Rename(f, VfxEdit.FindByName(f, "wind"), "gust");
        Assert.Equal(["gust", "gust", "other"], Named<VfxParticleSystem>(f, "sparks").Warps.ToArray());
        f = VfxEdit.Rename(f, VfxEdit.FindByName(f, "sparks"), "embers");
        Assert.Equal("embers", Named<VfxLight>(f, "glow").Parent);
        Assert.DoesNotContain(VfxLinter.Lint(f), d => d.Code is VfxRules.ParentNotInFile);
    }

    [Fact]
    public void RenameKeepsReferencesWhileTheNameIsStillTaken()
    {
        var f = VfxEdit.AddSection(Scene(), VfxBuilder.Dummy("root"));
        int second = f.Sections.Select((s, i) => (s, i)).Last(t => VfxEdit.NameOf(t.s) == "root").i;
        f = VfxEdit.Rename(f, second, "root2");
        Assert.Equal("root", Named<VfxSpacewarp>(f, "wind").Parent);
    }

    [Fact]
    public void RemoveClearsDanglingReferences()
    {
        var f = Scene();
        f = VfxEdit.RemoveSection(f, VfxEdit.FindByName(f, "wind"));
        Assert.Equal(["other"], Named<VfxParticleSystem>(f, "sparks").Warps.ToArray());
        f = VfxEdit.RemoveSection(f, VfxEdit.FindByName(f, "sparks"));
        Assert.Equal("root", Named<VfxLight>(f, "glow").Parent); // takes over the removed object's parent
        f = VfxEdit.RemoveSection(f, VfxEdit.FindByName(f, "root"));
        Assert.Equal("Scene Root", Named<VfxLight>(f, "glow").Parent);
        Assert.DoesNotContain(VfxLinter.Lint(f), d => d.Code is VfxRules.ParentNotInFile or VfxRules.MissingSpacewarp);
    }

    [Fact]
    public void MakeNamesUniqueKeepsReferencesResolving()
    {
        var f = VfxEdit.AddSection(Scene(), VfxBuilder.Spacewarp("WIND"));
        f = VfxEdit.AddSection(f, VfxBuilder.Dummy("ROOT"));
        var fixedFile = VfxLinter.MakeNamesUnique(f);
        Assert.Equal("wind", VfxEdit.NameOf(fixedFile.Sections[VfxEdit.FindByName(f, "wind")]));
        Assert.Equal("root", Named<VfxSpacewarp>(fixedFile, "wind").Parent);
        Assert.Equal(["wind", "WIND", "other"], Named<VfxParticleSystem>(fixedFile, "sparks").Warps.ToArray());
        var diags = VfxLinter.Lint(fixedFile);
        Assert.DoesNotContain(diags, d => d.Code is VfxRules.SectionName or VfxRules.ParentNotInFile);
    }

    [Fact]
    public void EndFrameRuleConsidersNonMeshObjects()
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.Dummy("d", frames: 30));
        Assert.Contains(VfxLinter.Lint(f with { EndFrame = 5 }), d => d.Code == VfxRules.EndFrameMismatch);
        Assert.DoesNotContain(VfxLinter.Lint(f with { EndFrame = 29 }), d => d.Code == VfxRules.EndFrameMismatch);
        var fl = VfxEdit.AddSection(VfxBuilder.NewFile(), VfxBuilder.Light("l", frames: 20));
        Assert.Contains(VfxLinter.Lint(fl with { EndFrame = 40 }), d => d.Code == VfxRules.EndFrameMismatch);
        var fp = VfxEdit.AddSection(VfxBuilder.NewFile(), VfxBuilder.ImageMaterial("a.tga"));
        fp = VfxEdit.AddSection(fp, VfxBuilder.ParticleSystem("p", 0, frames: 10) with { StartTime = 10 });
        Assert.DoesNotContain(VfxLinter.Lint(fp with { EndFrame = 19 }), d => d.Code == VfxRules.EndFrameMismatch);
        Assert.Contains(VfxLinter.Lint(fp with { EndFrame = 9 }), d => d.Code == VfxRules.EndFrameMismatch);
    }
}
