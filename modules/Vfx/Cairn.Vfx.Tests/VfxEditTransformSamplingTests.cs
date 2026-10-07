using Cairn.Vfx.Animation;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

/// <summary>Transform conversions and resampling checked against <see cref="VfxSampler"/>.</summary>
public sealed class VfxEditTransformSamplingTests(ITestOutputHelper output)
{
    // Relative position tolerance (x max(1, largest coordinate)); covers requantisation and float reassociation.
    private const float Tolerance = 1e-3f;

    private static IEnumerable<(string Name, VfxFile File)> Stock()
    {
        if (LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) yield break;
        foreach (var path in Directory.GetFiles(dir, "*.vfx").Order(StringComparer.OrdinalIgnoreCase))
        {
            var f = VfxReader.Read(File.ReadAllBytes(path), Path.GetFileName(path));
            yield return (Path.GetFileName(path), f.Version == VfxVersion.Current ? f : VfxUpgrade.ToCurrent(f));
        }
    }

    private static IEnumerable<int> MeshSections(VfxFile f, Func<VfxMesh, bool> pick) =>
        Enumerable.Range(0, f.Sections.Length).Where(i => f.Sections[i] is VfxMesh m && m.Frames.Length > 0 && m.NumVertices > 0 && pick(m));

    /// <summary>Samples section <paramref name="section"/> of both files at every frame time (and midway when asked) and compares positions.</summary>
    private static int AssertSameSamples(VfxFile before, VfxFile after, int section, string label, bool midFrames = false)
    {
        int ord = before.Sections.Take(section).Count(s => s is VfxMesh);
        VfxSampler sa = new(before), sb = new(after);
        var view = sa.Meshes[ord];
        Assert.Same(before.Sections[section], view.Mesh);
        Assert.Same(after.Sections[section], sb.Meshes[ord].Mesh);
        int count = view.Positions[0].Length;
        VfxMeshSample x = new(), y = new();
        int checkedFrames = 0;
        float step = midFrames ? 0.5f : 1f;
        for (float i = 0; i < view.FrameCount; i += step)
        {
            float frame = view.StartSeconds * VfxTime.FramesPerSecond + i * VfxTime.FramesPerSecond / view.Fps;
            bool ok = sa.SampleMesh(ord, frame, x);
            Assert.Equal(ok, sb.SampleMesh(ord, frame, y));
            if (!ok) continue;
            float extent = 1;
            for (int v = 0; v < count; v++) extent = Math.Max(extent, Math.Max(Math.Abs(x.Positions[v].X), Math.Max(Math.Abs(x.Positions[v].Y), Math.Abs(x.Positions[v].Z))));
            float tol = Tolerance * extent;
            for (int v = 0; v < count; v++)
                Assert.True(Vector3.Distance(x.Positions[v], y.Positions[v]) <= tol,
                    $"{label}: frame {i} vertex {v}: {x.Positions[v]} vs {y.Positions[v]} (tol {tol})");
            checkedFrames++;
        }
        return checkedFrames;
    }

    [Fact]
    public void StockKeyframedMeshesBakeToSamePositions()
    {
        int meshes = 0, frames = 0;
        foreach (var (name, f) in Stock())
            foreach (int i in MeshSections(f, m => m.IsKeyframed is 1 && !m.IsMorph))
            {
                var g = VfxEdit.ToPerFrameTransforms(f, i);
                Assert.Null(((VfxMesh)g.Sections[i]).Keys);
                frames += AssertSameSamples(f, g, i, $"{name}#{i}");
                meshes++;
            }
        output.WriteLine($"keyframed -> per-frame: {meshes} meshes, {frames} frames");
    }

    [Fact]
    public void StockMeshesToMorphKeepSampledPositions()
    {
        int meshes = 0, frames = 0;
        foreach (var (name, f) in Stock())
            foreach (int i in MeshSections(f, m => !m.IsMorph))
            {
                var g = VfxEdit.ToMorph(f, i);
                var m = (VfxMesh)g.Sections[i];
                Assert.True(m.IsMorph);
                Assert.Null(m.Keys);
                Assert.All(m.Frames, fr => Assert.Null(fr.Transform));
                frames += AssertSameSamples(f, g, i, $"{name}#{i}");
                meshes++;
            }
        output.WriteLine($"ToMorph: {meshes} meshes, {frames} frames");
    }

    private static VfxFile WithMesh(VfxMeshBuilder b, out int index)
    {
        var f = VfxEdit.AddSection(VfxBuilder.NewFile(), VfxBuilder.ImageMaterial("a.tga"));
        f = VfxEdit.AddSection(f, b.Build());
        index = VfxEdit.FindByName(f, b.Name);
        return f;
    }

    private static VfxMeshBuilder Quad(string name, int frames) => new()
    {
        Name = name,
        Frames = [[new(-1, 0, -1), new(1, 0, -1), new(1, 0, 1), new(-1, 0, 1)]],
        Triangles = [(0, 2, 1), (0, 3, 2)],
        Uvs = [new(0, 0), new(1, 1), new(1, 0), new(0, 0), new(0, 1), new(1, 1)],
        MaterialIndices = [0],
        FrameCount = frames,
    };

    private static VfxKeyLists CurvedKeys(Vector3 scale) => new(
        [new(0, new(0, 0, 0), new(0, 0, 0), new(1, 2, 0)), new(1280, new(4, 0, 1), new(3, -2, 1), new(4, 0, 1)), new(2560, new(0, 1, 0), new(0, 1, 0), new(0, 1, 0))],
        [new(0, Quaternion.Identity, 0, 0, 0, 0, 0.5f), new(2560, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2.5f), 0, 0, 0, 0.3f, 0)],
        [new(0, Vector3.One, Vector3.One, Vector3.One), new(2560, scale, scale, scale)]);

    [Theory]
    [InlineData(2f, 2f, 2f, 0.7f)]   // uniform key scale: pivot composed into the TRS
    [InlineData(1f, 3f, 0.5f, 0f)]   // non-uniform key scale, unrotated pivot: composed
    [InlineData(1f, 3f, 0.5f, 0.7f)] // non-uniform key scale over a rotated pivot: pivot baked into positions
    public void CurvedKeysWithPivotBakeToSamePositions(float sx, float sy, float sz, float pivotAngle)
    {
        var b = Quad("Keyed", 9);
        b.Keys = CurvedKeys(new(sx, sy, sz));
        b.Pivot = new VfxTransform(new(0.5f, 1, -2), Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 1, 0)), pivotAngle), new(1.5f, 1, 0.5f));
        var f = WithMesh(b, out int i);
        Assert.Equal(9, AssertSameSamples(f, VfxEdit.ToPerFrameTransforms(f, i), i, "keyed"));
        Assert.Equal(9, AssertSameSamples(f, VfxEdit.ToMorph(f, i), i, "keyed morph"));
    }

    [Fact]
    public void PerFrameTransformsSurviveToMorphAndToKeyframes()
    {
        var f = WithMesh(Quad("Spin", 5), out int i);
        for (int fr = 0; fr < 5; fr++)
            f = VfxEdit.SetStaticTransform(f, i, new VfxTransform(new(fr, 0, -fr), Quaternion.CreateFromAxisAngle(Vector3.UnitY, fr * 0.6f), new(1 + fr * 0.25f)), fr);
        f = VfxEdit.SetFps(f, i, 10);
        f = VfxEdit.SetStartTime(f, i, 0.2f);
        Assert.Equal(5, AssertSameSamples(f, VfxEdit.ToMorph(f, i), i, "morph"));
        // Keys at each frame's tick (fps 10, start 0.2 s) with linear absolute control points sample like the per-frame transforms.
        var keyed = VfxEdit.ToKeyframes(f, i);
        Assert.Equal(5, AssertSameSamples(f, keyed, i, "keys"));
        Assert.Equal(5, AssertSameSamples(f, VfxEdit.ToPerFrameTransforms(keyed, i), i, "keys -> per-frame"));

        // Translation only: the keyed Bezier is linear between keys, so it also matches the per-frame lerp midway.
        var g = WithMesh(Quad("Slide", 4), out int j);
        for (int fr = 0; fr < 4; fr++) g = VfxEdit.SetStaticTransform(g, j, VfxBuilder.Identity with { Translation = new(fr * fr, 2 * fr, 0) }, fr);
        Assert.Equal(8, AssertSameSamples(g, VfxEdit.ToKeyframes(g, j), j, "slide", midFrames: true));
    }

    [Fact]
    public void ResampleInterpolatesMorphPositionsAndTransforms()
    {
        var mb = Quad("Morph", 2);
        mb.Frames = [mb.Frames[0], mb.Frames[0].Select(p => p * 3 + new Vector3(0, 2, 0)).ToArray()];
        var f = WithMesh(mb, out int morph);
        var g = VfxEdit.SetFrameCount(f, morph, 3, VfxFrameFill.Resample);
        var m = (VfxMesh)g.Sections[morph];
        var a = ((VfxMesh)f.Sections[morph]).DecodePositions(0)!;
        var c = ((VfxMesh)f.Sections[morph]).DecodePositions(1)!;
        var mid = m.DecodePositions(1)!;
        for (int v = 0; v < a.Length; v++) Assert.True(Vector3.Distance(mid[v], Vector3.Lerp(a[v], c[v], 0.5f)) < 1e-3f);
        Assert.Same(((VfxMesh)f.Sections[morph]).Frames[1].Positions, m.Frames[2].Positions);

        var q = WithMesh(Quad("Xf", 2), out int qi);
        var t1 = new VfxTransform(new(2, 4, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.2f), new(3));
        q = VfxEdit.SetStaticTransform(q, qi, t1, 1);
        var r = VfxEdit.SetFrameCount(q, qi, 3, VfxFrameFill.Resample);
        var t = ((VfxMesh)r.Sections[qi]).Frames[1].Transform!;
        Assert.True(Vector3.Distance(t.Translation, new(1, 2, 0)) < 1e-5f);
        Assert.True(Vector3.Distance(t.Scale, new(2)) < 1e-5f);
        Assert.True(Math.Abs(Quaternion.Dot(t.Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f))) > 1 - 1e-5f);
    }
}
