using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Imaging;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;
using Cairn.Rfa.SampleGen;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Checks the generated sample set. The committed files under samples/ come from
/// <c>dotnet run --project tools/Cairn.Rfa.SampleGen -- samples</c>; these tests regenerate them
/// into a scratch folder so they pass whether or not the repository is present.
/// </summary>
public class SampleTests(ITestOutputHelper output)
{
    private static TempFolder Generate()
    {
        var temp = new TempFolder();
        SampleSet.WriteAll(temp.Path);
        return temp;
    }

    private static V3dFile Mesh(TempFolder temp) => V3dReader.ReadFile(temp.File(SampleSet.MeshFile));

    /// <summary>
    /// The context the App builds for a clip previewed on the sample mesh (no tables, no library), plus
    /// the idle as the reference clip the library would supply for RFA023.
    /// </summary>
    private static ClipLintContext Context(TempFolder temp, string clipFile) =>
        ClipLintContextBuilder.Build(clipFile, temp.File(clipFile), Mesh(temp), SampleSet.MeshFile, library: null, usage: null) with
        {
            ReferenceClip = RfaReader.ReadFile(temp.File(SampleSet.IdleFile)),
            ReferenceClipName = SampleSet.IdleFile,
        };

    private static List<string> Names(string folder) =>
        [.. Directory.EnumerateFiles(folder).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(d => $"{d.Code} {d.Message}"));

    [Fact]
    public void TheMeshHasNoProblemsAtAll()
    {
        using var temp = Generate();
        var mesh = Mesh(temp);
        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = temp.Path });
        var diagnostics = MeshLinter.Analyze(mesh, new MeshLintContext { FileName = temp.File(SampleSet.MeshFile), Resolver = resolver });
        Assert.True(diagnostics.Count == 0, Describe(diagnostics));

        Assert.Equal(V3dKind.Character, mesh.Kind);
        Assert.Equal(SampleFigure.Bones.Select(b => b.Name), mesh.Bones.Select(b => b.Name.Text));
        Assert.Equal(["head", "torso", "legs"], mesh.CollisionSpheres.Select(s => s.Name.Text));
        var lod = mesh.Submeshes.Single().Lods.Single();
        Assert.Equal(["hand_grip"], lod.PropPoints.Select(p => p.Name.Text));
        Assert.Equal([SampleSet.TextureFile], lod.Textures.Select(t => t.FileName));
        Assert.Equal(File.ReadAllBytes(temp.File(SampleSet.MeshFile)), V3dWriter.Write(mesh));
    }

    [Fact]
    public void TheFigureStandsOnTheGroundFacingPlusZWithItsFacesOutward()
    {
        using var temp = Generate();
        var mesh = Mesh(temp);
        var skeleton = Skeleton.FromFile(mesh);
        float Height(string bone) => skeleton.RestWorld[skeleton.IndexOf(bone)].Position.Y;
        Assert.True(Height("head") > Height("spine") && Height("spine") > Height("pelvis")
            && Height("pelvis") > Height("shin_l") && Height("shin_l") > Height("foot_l"));
        Assert.True(skeleton.RestWorld[skeleton.IndexOf("hand_r")].Position.X > 0, "the right hand should be on +X");

        var sub = mesh.Submeshes.Single();
        Assert.Equal(0f, sub.AabbMin.Y);
        Assert.InRange(sub.AabbMax.Y, 1.7f, 1.9f);

        var batch = sub.Lods.Single().Batches.Single();
        int facing = 0;
        foreach (var t in batch.Triangles)
        {
            var a = batch.Positions[t.A];
            var cross = Vector3.Cross(batch.Positions[t.B] - a, batch.Positions[t.C] - a);
            // Front faces: cross(B - A, C - A) agrees with the stored normals.
            Assert.True(Vector3.Dot(cross, batch.Normals[t.A]) > 0f, $"triangle {t} is inside out");
            // The face tile (top-right of the atlas) is only on the head's front, which looks down +Z.
            var uv = batch.TexCoords[t.A];
            if (uv.X > 0.75f && uv.Y < 0.25f)
            {
                Assert.Equal(Vector3.UnitZ, batch.Normals[t.A]);
                facing++;
            }
        }
        Assert.Equal(2, facing);
    }

    [Fact]
    public void TheCleanClipsHaveNoProblemsOnTheMesh()
    {
        using var temp = Generate();
        int bones = Mesh(temp).Bones.Length;
        foreach (string name in SampleSet.CleanClips)
        {
            var clip = RfaReader.ReadFile(temp.File(name));
            var diagnostics = ClipLinter.Analyze(clip, Context(temp, name));
            Assert.True(diagnostics.Count == 0, $"{name}: {Describe(diagnostics)}");

            Assert.Equal(bones, clip.BoneCount);
            Assert.Equal(RfaClip.TicksPerFrame, clip.StartTime);
            Assert.All(clip.Bones, t =>
            {
                Assert.True(t.RotationKeys.Length >= 2 && t.PositionKeys.Length >= 2, name);
                Assert.All(t.PositionKeys, k => Assert.True(k.InControl != Vector3.Zero && k.OutControl != Vector3.Zero, name));
            });
            Assert.Equal(File.ReadAllBytes(temp.File(name)), RfaWriter.Write(clip));
        }
    }

    [Fact]
    public void TheLoopingClipsEndExactlyWhereTheyStart()
    {
        using var temp = Generate();
        foreach (string name in new[] { SampleSet.IdleFile, SampleSet.WalkFile })
        {
            var clip = RfaReader.ReadFile(temp.File(name));
            int moving = 0;
            foreach (var track in clip.Bones)
            {
                Assert.Equal(clip.StartTime, track.RotationKeys[0].Time);
                Assert.Equal(clip.EndTime, track.RotationKeys[^1].Time);
                Assert.Equal(track.RotationKeys[0] with { Time = 0 }, track.RotationKeys[^1] with { Time = 0 });
                Assert.Equal(track.PositionKeys[0].Position, track.PositionKeys[^1].Position);
                Assert.Equal(ClipSampler.SampleBone(track, clip.StartTime), ClipSampler.SampleBone(track, clip.EndTime));
                if (track.RotationKeys.Length > 2) moving++;
            }
            Assert.True(moving >= 8, $"{name} should move most of the body ({moving} bones move)");
        }

        // The walk swings each leg forward and back by about 25 degrees.
        var walk = RfaReader.ReadFile(temp.File(SampleSet.WalkFile));
        var swing = walk.Bones[SampleFigure.ThighL].RotationKeys.Select(k => Quat.ToEulerDegrees(ClipSampler.KeyRotation(k)).X).ToList();
        Assert.InRange(swing.Max(), 20f, 30f);
        Assert.InRange(swing.Min(), -30f, -20f);
    }

    [Fact]
    public void TheWaveIsAnEasedActionOnTheRightArm()
    {
        using var temp = Generate();
        var wave = RfaReader.ReadFile(temp.File(SampleSet.WaveFile));
        int[] arm = [SampleFigure.UpperArmR, SampleFigure.ForearmR, SampleFigure.HandR];
        for (int b = 0; b < wave.BoneCount; b++)
        {
            Assert.Equal(arm.Contains(b) ? ClipBlender.FullWeight : 0f, wave.Bones[b].Weight);
        }
        foreach (int b in arm)
        {
            var keys = wave.Bones[b].RotationKeys;
            Assert.True(keys.Length >= 4);
            Assert.All(keys.Skip(1), k => Assert.NotEqual(0, k.EaseIn));
            Assert.All(keys.SkipLast(1), k => Assert.NotEqual(0, k.EaseOut));
        }
        Assert.True(wave.RampIn > 0 && wave.RampOut > 0 && wave.RampIn + wave.RampOut < wave.Duration);

        // Mid-wave the right hand is above the shoulder.
        var skeleton = Skeleton.FromFile(Mesh(temp));
        var locals = new Rigid[wave.BoneCount];
        ClipSampler.SampleLocals(wave, SampleClips.Frame(28), locals);
        var world = new Rigid[wave.BoneCount];
        foreach (int b in skeleton.EvaluationOrder)
        {
            int p = skeleton.EffectiveParents[b];
            world[b] = p < 0 ? locals[b] : world[p].Compose(locals[b]);
        }
        Assert.True(world[SampleFigure.HandR].Position.Y > world[SampleFigure.UpperArmR].Position.Y + 0.2f);
    }

    [Fact]
    public void TheBrokenClipTripsExactlyTheRulesTheReadmeLists()
    {
        using var temp = Generate();
        var clip = RfaReader.ReadFile(temp.File(SampleSet.BrokenFile));
        Assert.Equal(Mesh(temp).Bones.Length, clip.BoneCount);

        var diagnostics = ClipLinter.Analyze(clip, Context(temp, SampleSet.BrokenFile));
        foreach (var d in diagnostics) output.WriteLine($"{d.Code} {d.Severity}: {d.Message}");
        var codes = diagnostics.Select(d => d.Code).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(SampleClips.BrokenRules.Select(r => r.Code).Order(StringComparer.Ordinal), codes);
        Assert.True(diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) >= 4);
        Assert.True(diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning) >= 6);
        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Info);

        // Without the library (no reference clip) everything but RFA023 still fires.
        var bare = ClipLinter.Analyze(clip, ClipLintContextBuilder.Build(
            SampleSet.BrokenFile, temp.File(SampleSet.BrokenFile), Mesh(temp), SampleSet.MeshFile, null, null));
        Assert.Equal(codes.Where(c => c != ClipRules.ProportionsDiffer), bare.Select(d => d.Code).Distinct().Order(StringComparer.Ordinal));

        string readme = File.ReadAllText(temp.File(SampleSet.ReadmeFile));
        Assert.All(codes, code => Assert.Contains($"| {code} |", readme));
    }

    [Fact]
    public void TheTextureIsA64By64TwentyFourBitTgaWithAFace()
    {
        using var temp = Generate();
        var info = ImageProbe.ProbeFile(temp.File(SampleSet.TextureFile));
        Assert.Equal(ImageContainer.Tga, info.Container);
        Assert.Equal(64, info.Width);
        Assert.Equal(64, info.Height);
        Assert.Equal(EngineFormat.Rgb888, info.Format);

        var image = ImageDecoder.DecodeFile(temp.File(SampleSet.TextureFile));
        var skin = image.Get(50, 2);  // the face tile (column 3, row 0), above the eyes
        var eye = image.Get(52, 5);   // the left-hand eye
        Assert.True(skin.R > 150 && eye.R < 80, $"skin {skin}, eye {eye}");
        Assert.Equal(image.Get(52, 5), image.Get(58, 5)); // the eyes match, so the face is the right way up and centred
    }

    [Fact]
    public void EveryNameFitsTheEngine()
    {
        using var temp = Generate();
        foreach (string path in Directory.EnumerateFiles(temp.Path))
            Assert.True(Path.GetFileName(path).Length <= ClipRules.MaxFileNameLength, Path.GetFileName(path));
        Assert.True(SampleSet.TextureFile.Length < V3dMaterial.NameSize);
        Assert.All(SampleFigure.Bones, b => Assert.True(b.Name.Length < V3dBone.NameSize, b.Name));
    }

    [Fact]
    public void TheGeneratorIsDeterministic()
    {
        using var first = Generate();
        using var second = Generate();
        var names = Names(first.Path);
        Assert.Equal(names, Names(second.Path));
        foreach (string name in names)
            Assert.Equal(File.ReadAllBytes(first.File(name)), File.ReadAllBytes(second.File(name)));
    }

    [Fact]
    public void TheCommittedSamplesMatchWhatTheGeneratorProduces()
    {
        string? committed = TestPaths.Samples;
        if (committed is null) return;

        using var temp = Generate();
        var generated = Names(temp.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(temp.Path))
        {
            string name = Path.GetFileName(path);
            string other = Path.Combine(committed, name);
            Assert.True(File.Exists(other), $"samples/{name} is missing — re-run the sample generator");
            if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                // Text: compared with line endings normalised, so a CRLF checkout still matches.
                Assert.Equal(File.ReadAllText(path), File.ReadAllText(other).Replace("\r\n", "\n", StringComparison.Ordinal));
            }
            else
            {
                Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(other));
            }
        }
        foreach (string path in Directory.EnumerateFiles(committed))
            Assert.True(generated.Contains(Path.GetFileName(path)), $"samples/{Path.GetFileName(path)} is not produced by the generator — remove it or regenerate");
    }
}
