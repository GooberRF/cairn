using Cairn.Formats.Rfl;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;

namespace Cairn.Lvl.Tests;

/// <summary>The corona fit of Red Faction II lighting on damaged numbers: a non-finite value is left out, never thrown on.</summary>
public sealed class Rf2LightingTests
{
    private static readonly RflColor Grey = new(100, 100, 100, 255);

    /// <summary>A lit floor of 2 x 2 m squares, two triangles each, every corner baked grey (240 corners).</summary>
    private static Rf2VertexLighting Floor(Func<Vector3, Vector3>? damage = null)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<Rf2LitTriangle>();
        for (int x = 0; x < 5; x++)
        {
            for (int z = 0; z < 4; z++)
            {
                int a = vertices.Count;
                vertices.Add(new Vector3(x * 2, 0, z * 2));
                vertices.Add(new Vector3(x * 2, 0, z * 2 + 2));
                vertices.Add(new Vector3(x * 2 + 2, 0, z * 2));
                vertices.Add(new Vector3(x * 2 + 2, 0, z * 2 + 2));
                triangles.Add(new Rf2LitTriangle(a, a + 1, a + 2, Grey, Grey, Grey));
                triangles.Add(new Rf2LitTriangle(a + 1, a + 3, a + 2, Grey, Grey, Grey));
            }
        }
        if (damage is not null) vertices[0] = damage(vertices[0]);
        return new Rf2VertexLighting { Entries = [new Rf2LitEntry { Vertices = [.. vertices], Triangles = [.. triangles] }] };
    }

    private static RfCorona Corona(Vector3 position, float intensity) => new() { Position = position, Intensity = intensity, Color = new RflColor(255, 255, 255, 255) };

    [Fact]
    public void A_finite_floor_and_corona_give_a_fit()
    {
        var fit = Rf2Lighting.FitCoronas(Floor(), [], [Corona(new Vector3(5, 3, 4), 1f)], default);
        Assert.NotNull(fit);
        Assert.InRange(fit.Value.Range, 10f, 60f);
        Assert.InRange(fit.Value.Scale, 0.1f, 3f);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_non_finite_corona_intensity_is_left_out_of_the_fit(float intensity)
    {
        var fit = Rf2Lighting.FitCoronas(Floor(), [], [Corona(new Vector3(5, 3, 4), 1f), Corona(new Vector3(1, 3, 1), intensity)], default);
        Assert.NotNull(fit);
        Assert.True(float.IsFinite(fit.Value.Range) && float.IsFinite(fit.Value.Scale));
        Assert.Null(Rf2Lighting.FitCoronas(Floor(), [], [Corona(new Vector3(5, 3, 4), intensity)], default));
    }

    [Fact]
    public void A_non_finite_corona_position_or_corner_never_throws()
    {
        Assert.Null(Rf2Lighting.FitCoronas(Floor(), [], [Corona(new Vector3(float.NaN, 3, 4), 1f)], default));
        var fit = Rf2Lighting.FitCoronas(Floor(v => v with { Y = float.NaN }), [], [Corona(new Vector3(5, 3, 4), 1f)], default);
        Assert.NotNull(fit);
        Assert.True(float.IsFinite(fit.Value.Range) && float.IsFinite(fit.Value.Scale));
        // Huge but finite numbers overflow inside the fit; no fit is better than a NaN one.
        var huge = Rf2Lighting.FitCoronas(Floor(), [], [Corona(new Vector3(3e38f, 3e38f, -3e38f), 3e38f)], default);
        Assert.True(huge is null || float.IsFinite(huge.Value.Range) && float.IsFinite(huge.Value.Scale));
        int uid = 1;
        var fill = Rf2Lighting.FillLights(Floor(v => v with { X = float.PositiveInfinity }), [], [Corona(new Vector3(5, 3, 4), 1f)], ref uid, default);
        Assert.All(fill, l => Assert.True(float.IsFinite(l.Position.X) && float.IsFinite(l.Position.Y) && float.IsFinite(l.Position.Z)));
    }

    [Fact]
    public void A_real_level_with_one_nan_corona_intensity_converts_to_every_target()
    {
        if (Rf2Samples.ReduxFolder is not { } folder) return;
        string path = Path.Combine(folder, "dmc14.rfl");
        if (!File.Exists(path)) return;
        var level = RflLevelReader.ReadFile(path);
        if (level.Objects.Coronas.IsEmpty) return;
        var bad = level with { Objects = level.Objects with { Coronas = level.Objects.Coronas.SetItem(0, level.Objects.Coronas[0] with { Intensity = float.NaN }) } };
        foreach (var target in (LvlConvertTarget[])[LvlConvertTarget.Glb, LvlConvertTarget.Obj, LvlConvertTarget.V3m, LvlConvertTarget.Rfg])
        {
            var c = LvlConverter.Convert(LvlConvertInput.FromLevel(bad), new LvlConvertOptions { Target = target, ExportTextures = false }, "dmc14" + LvlConvertOptions.ExtensionOf(target));
            Assert.True(c.HasOutput, $"{target}: {c.Report.ToText()}");
        }
    }
}
