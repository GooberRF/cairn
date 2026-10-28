using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;
using Xunit.Abstractions;

namespace Cairn.Lvl.Tests;

/// <summary>
/// Red Faction II levels on the real files: REDUX's 101 research copies and the 105 levels inside the RF2 install's
/// All_Levels packfile. The normal run reads a few chosen levels; <c>CAIRN_LVL_CORPUS=1</c> reads all of them and
/// checks the totals the format research counted over the 105 distinct levels. Each source passes when absent.
/// </summary>
public sealed class Rf2LevelSampleTests(ITestOutputHelper output)
{
    private static IEnumerable<LvlSamples.Sample> Chosen(IReadOnlyList<LvlSamples.Sample> all) => LvlSamples.CorpusEnabled
        ? all
        : all.Where(s => Rf2Samples.Bounded.Contains(Path.GetFileName(s.Name), StringComparer.OrdinalIgnoreCase));

    private void ReadAll(IEnumerable<LvlSamples.Sample> samples, Action<RfLevel>? each = null)
    {
        var failures = new List<string>();
        int n = 0;
        foreach (var s in samples)
        {
            var level = RflLevelReader.Read(s.Load(), Path.GetFileName(s.Name));
            Assert.True(level.IsRf2, s.Name);
            foreach (string issue in Rf2Samples.Unexplained(level)) failures.Add($"{s.Name}: {issue}");
            each?.Invoke(level);
            n++;
        }
        output.WriteLine($"{n} RF2 levels read");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(40)));
    }

    [Fact]
    public void Redux_rf2_levels_read_with_every_section_consumed_or_kept_for_a_reason() => ReadAll(Chosen(Rf2Samples.ReduxLevels()));

    [Fact]
    public void Texture_packs_and_the_clutter_table_are_found_for_real_levels()
    {
        if (Rf2Samples.ReduxFolder is { } folder && File.Exists(Path.Combine(folder, "dmc13.rfl")))
        {
            var packs = Cairn.Lvl.Conversion.Rf2LevelTextures.ForFile(Path.Combine(folder, "dmc13.rfl"));
            Assert.Equal("dmc13.peg", packs.PackNames[0], StringComparer.OrdinalIgnoreCase);
            var level = RflLevelReader.ReadFile(Path.Combine(folder, "dmc13.rfl"));
            string texture = level.StaticGeometry.Textures.First(t => !t.Equals("misc-static.tga", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(packs.Decode(texture));
        }
        if (Cairn.Workspace.LocalPaths.Rf2GameDirectory is { } rf2 && Rf2Samples.Group("Permanent_data") is not null)
        {
            var permanent = Cairn.Lvl.Conversion.Rf2LevelTextures.PermanentPacks(rf2);
            Assert.Contains(permanent, p => p.Name.Equals("misc.peg", StringComparison.OrdinalIgnoreCase));
            var set = Cairn.Lvl.Conversion.Rf2LevelTextures.ForLevel("dmc13.rfl", [], permanent);
            Assert.NotNull(set.Decode("misc-static.tga"));
            var clutter = Cairn.Lvl.Conversion.Rf2ClutterTable.FromGameFolder(rf2);
            Assert.NotNull(clutter);
            Assert.Equal("MineLight01.v3m", clutter!["minelight01"].ConvertedMeshFile);
            output.WriteLine($"{permanent.Count} shared packs, {clutter.Count} clutter classes");
        }
    }

    [Fact]
    public void All_levels_packfile_levels_read_with_every_section_consumed_and_match_the_research_totals()
    {
        var all = Rf2Samples.PackedLevels();
        if (all.Count == 0) return;
        long brushes = 0, movers = 0, lights = 0, coronas = 0, decals = 0, staticFaces = 0, brushFaces = 0, moverFaces = 0;
        int levels = 0, noBitmapCoronas = 0, shapeOnePush = 0;
        ReadAll(Chosen(all), level =>
        {
            levels++;
            brushes += level.Brushes.Length;
            movers += level.Movers.Length;
            lights += level.Objects.Lights.Length;
            coronas += level.Objects.Coronas.Length;
            decals += level.Objects.Decals.Length;
            staticFaces += level.StaticGeometry.Faces.Length;
            brushFaces += level.Brushes.Sum(b => (long)b.Geometry.Faces.Length);
            moverFaces += level.Movers.Sum(b => (long)b.Geometry.Faces.Length);
            noBitmapCoronas += level.Objects.Coronas.Count(c => c.CoronaBitmap.Length == 0);
            shapeOnePush += level.Objects.PushRegions.Count(p => p.Shape == 1);
            Assert.NotNull(level.Rf2?.Properties);
            Assert.NotNull(level.Rf2?.VertexLighting);
        });
        output.WriteLine($"brushes {brushes}, movers {movers}, lights {lights}, coronas {coronas} ({noBitmapCoronas} without a bitmap), decals {decals}, "
            + $"faces {staticFaces} / {brushFaces} / {moverFaces}, shape-1 push regions {shapeOnePush}");
        Assert.True(noBitmapCoronas > 0 || !Chosen(all).Any(s => s.Name.EndsWith("l10s1c.rfl", StringComparison.OrdinalIgnoreCase)));
        if (levels != 105) return;
        // The totals of the format research over the 105 distinct levels.
        Assert.Equal(87_723, brushes);
        Assert.Equal(1_870, movers);
        Assert.Equal(8_600, lights);
        Assert.Equal(6_149, coronas);
        Assert.Equal(17_213, decals);
        Assert.Equal(950_003, staticFaces);
        Assert.Equal(886_212, brushFaces);
        Assert.Equal(36_309, moverFaces);
        Assert.Equal(37, noBitmapCoronas);
        Assert.Equal(4, shapeOnePush);
    }
}
