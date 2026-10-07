using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Tbl;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;
using Cairn.Rfa.SampleGen;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// <see cref="NewClip"/>: a clip made from nothing holds a correct starting pose on every bone (the bind
/// pose, or a reference clip's pose at a time), lints clean against the mesh, and round-trips through the
/// writer and reader. Uses the synthetic sample figure; the stock checks read the corpus in place and pass
/// trivially without it.
/// </summary>
public class NewClipTests(ITestOutputHelper output)
{
    /// <summary>Quantisation leaves a rotation within ~0.01 degrees; sampling the int16 slerp adds a little.</summary>
    private const float AngleTolerance = 0.02f;

    private static V3dFile SampleMesh() => V3dBuilder.Build(SampleFigure.Description(SampleSet.SubmeshName, SampleSet.TextureFile));

    private static ClipLintContext Context(V3dFile mesh, string meshName, string fileName, RfaClip? reference = null, string? referenceName = null) =>
        new()
        {
            FileName = fileName,
            PreviewMesh = mesh,
            PreviewMeshName = meshName,
            Skeleton = Skeleton.FromFile(mesh),
            ReferenceClip = reference,
            ReferenceClipName = referenceName,
        };

    private static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(d => $"{d.Code} {d.Message}"));

    /// <summary>Every structural invariant the class promises, whatever the options.</summary>
    private static void AssertWellFormed(RfaClip clip, Skeleton skeleton, NewClipOptions options)
    {
        int start = options.StartTime, end = options.StartTime + options.Length;
        Assert.Equal(skeleton.Count, clip.BoneCount);
        Assert.Equal(options.Version, clip.Version);
        Assert.Equal(start, clip.StartTime);
        Assert.Equal(end, clip.EndTime);
        Assert.Equal(options.RampIn, clip.RampIn);
        Assert.Equal(options.RampOut, clip.RampOut);
        Assert.True(clip.Morph.IsEmpty);
        Assert.Equal(Quaternion.Identity, clip.TotalRotation);
        Assert.Equal(Vector3.Zero, clip.TotalTranslation);
        foreach (var track in clip.Bones)
        {
            Assert.Equal(options.Weight, track.Weight);
            Assert.Equal([start, end], track.RotationKeys.Select(k => k.Time));
            Assert.Equal([start, end], track.PositionKeys.Select(k => k.Time));
            var (a, b) = (track.RotationKeys[0], track.RotationKeys[1]);
            Assert.Equal((a.X, a.Y, a.Z, a.W), (b.X, b.Y, b.Z, b.W));
            foreach (var k in track.RotationKeys)
            {
                Assert.Equal(0, k.EaseIn);
                Assert.Equal(0, k.EaseOut);
                Assert.Equal(0, k.Pad);
                long length = (long)k.X * k.X + (long)k.Y * k.Y + (long)k.Z * k.Z + (long)k.W * k.W;
                Assert.True(length <= 16383L * 16383L, $"quantised within unit length ({k.X}, {k.Y}, {k.Z}, {k.W})");
                Assert.True(length > 16380L * 16380L, "close to unit length");
            }
            Assert.Equal(track.PositionKeys[0].Position, track.PositionKeys[1].Position);
            foreach (var k in track.PositionKeys)
            {
                Assert.Equal(k.Position, k.InControl);
                Assert.Equal(k.Position, k.OutControl);
            }
        }
    }

    /// <summary>The sampled pose at start, middle and end equals the expected local pose of every bone.</summary>
    private static void AssertHolds(RfaClip clip, Func<int, (Quaternion Rotation, Vector3 Position)> expected)
    {
        foreach (float t in new[] { clip.StartTime, (clip.StartTime + clip.EndTime) / 2f, clip.EndTime })
        {
            for (int b = 0; b < clip.BoneCount; b++)
            {
                var got = ClipSampler.SampleBone(clip.Bones[b], t);
                var (rotation, position) = expected(b);
                Assert.True(Quat.AngleDegrees(got.Rotation, rotation) < AngleTolerance,
                    $"bone {b} at {t}: {Quat.AngleDegrees(got.Rotation, rotation):0.0000} degrees from the source pose");
                Assert.True(Vector3.Distance(got.Position, position) < 1e-6f, $"bone {b} at {t}: position {got.Position} vs {position}");
            }
        }
    }

    private static void AssertRoundTrips(RfaClip clip)
    {
        byte[] bytes = RfaWriter.Write(clip);
        var back = RfaReader.Read(bytes, "new.rfa");
        Assert.Equal(bytes, RfaWriter.Write(back));
        Assert.Equal(clip.BoneCount, back.BoneCount);
        for (int b = 0; b < clip.BoneCount; b++)
        {
            Assert.Equal(clip.Bones[b].Weight, back.Bones[b].Weight);
            Assert.Equal(clip.Bones[b].RotationKeys.ToArray(), back.Bones[b].RotationKeys.ToArray());
            Assert.Equal(clip.Bones[b].PositionKeys.ToArray(), back.Bones[b].PositionKeys.ToArray());
        }
        Assert.Equal((clip.Version, clip.StartTime, clip.EndTime, clip.RampIn, clip.RampOut), (back.Version, back.StartTime, back.EndTime, back.RampIn, back.RampOut));
    }

    [Fact]
    public void ABindPoseStateHoldsTheBindOnEveryBoneAndLintsClean()
    {
        var mesh = SampleMesh();
        var skeleton = Skeleton.FromFile(mesh);
        var options = NewClipOptions.ForKind(NewClipKind.State);
        var clip = NewClip.Create(mesh, options);

        Assert.Equal(0, options.RampIn);
        Assert.Equal(0, options.RampOut);
        Assert.Equal(160, options.StartTime);
        Assert.Equal(8, options.Version);
        Assert.Equal(10f, options.Weight);
        AssertWellFormed(clip, skeleton, options);
        AssertHolds(clip, b => (skeleton.RestLocal[b].Rotation, skeleton.RestLocal[b].Position));
        var diagnostics = ClipLinter.Analyze(clip, Context(mesh, SampleSet.MeshFile, "sample_figure_new.rfa"));
        Assert.True(diagnostics.Count == 0, Describe(diagnostics));
        AssertRoundTrips(clip);
    }

    [Fact]
    public void TheBoneOrderIsTheMeshesAndSkinningGivesTheBindMesh()
    {
        var mesh = SampleMesh();
        var skeleton = Skeleton.FromFile(mesh);
        var clip = NewClip.Create(skeleton, new NewClipOptions());
        var pose = new Pose(skeleton);
        pose.Sample(clip, clip.StartTime);
        for (int b = 0; b < skeleton.Count; b++)
        {
            Assert.True(Vector3.Distance(pose.World[b].Position, skeleton.RestWorld[b].Position) < 1e-5f, $"bone {b} joint");
            Assert.True(Quat.AngleDegrees(pose.World[b].Rotation, skeleton.RestWorld[b].Rotation) < AngleTolerance * 4, $"bone {b} frame");
        }
    }

    [Fact]
    public void AReferencePoseActionHoldsTheReferenceClipsPoseAtTheChosenTime()
    {
        var mesh = SampleMesh();
        var skeleton = Skeleton.FromFile(mesh);
        var walk = SampleClips.Walk();
        var idle = SampleClips.Idle();
        int poseTime = walk.StartTime + 11 * RfaClip.TicksPerFrame + 37; // between keys
        var options = NewClipOptions.ForKind(NewClipKind.Action) with
        {
            PoseClip = walk,
            PoseTime = poseTime,
            Length = 45 * RfaClip.TicksPerFrame,
            Version = 7,
            Weight = 5f,
            StartTime = 0,
        };
        var clip = NewClip.Create(skeleton, options);

        Assert.Equal(NewClipOptions.DefaultActionRamp, clip.RampIn);
        Assert.Equal(NewClipOptions.DefaultActionRamp, clip.RampOut);
        AssertWellFormed(clip, skeleton, options);
        AssertHolds(clip, b => (ClipSampler.SampleRotation(walk.Bones[b].RotationKeys.AsSpan(), poseTime),
            ClipSampler.SamplePosition(walk.Bones[b].PositionKeys.AsSpan(), poseTime)));
        // The walk moves: the chosen time's pose is not its start pose, so the time was honoured.
        Assert.True(Quat.AngleDegrees(ClipSampler.SampleBone(clip.Bones[SampleFigure.ThighL], 0).Rotation,
            ClipSampler.SampleRotation(walk.Bones[SampleFigure.ThighL].RotationKeys.AsSpan(), walk.StartTime)) > 1f);

        var diagnostics = ClipLinter.Analyze(clip, Context(mesh, SampleSet.MeshFile, "sample_figure_new.rfa", idle, SampleSet.IdleFile));
        Assert.True(diagnostics.Count == 0, Describe(diagnostics));
        AssertRoundTrips(clip);
    }

    [Fact]
    public void AReferenceTrackWithoutKeysFallsBackToTheBind()
    {
        var mesh = SampleMesh();
        var skeleton = Skeleton.FromFile(mesh);
        var idle = SampleClips.Idle();
        var gappy = idle with
        {
            Bones = idle.Bones
                .SetItem(SampleFigure.Head, idle.Bones[SampleFigure.Head] with { RotationKeys = [] })
                .SetItem(SampleFigure.HandL, idle.Bones[SampleFigure.HandL] with { PositionKeys = [] }),
        };
        var clip = NewClip.Create(skeleton, new NewClipOptions { PoseClip = gappy, PoseTime = gappy.StartTime });
        var head = ClipSampler.SampleBone(clip.Bones[SampleFigure.Head], clip.StartTime);
        var hand = ClipSampler.SampleBone(clip.Bones[SampleFigure.HandL], clip.StartTime);
        Assert.True(Quat.AngleDegrees(head.Rotation, skeleton.RestLocal[SampleFigure.Head].Rotation) < AngleTolerance);
        Assert.Equal(idle.Bones[SampleFigure.Head].PositionKeys[0].Position, head.Position);
        Assert.Equal(skeleton.RestLocal[SampleFigure.HandL].Position, hand.Position);
        Assert.DoesNotContain(ClipLinter.AnalyzeStructure(clip), d => d.Severity != DiagnosticSeverity.Info);
    }

    [Fact]
    public void AReferenceKeyThatNormalisesToAWholeNumberIsStillStoredWithinUnitLength()
    {
        // ult2_stand.rfa's bone 16 starts at (-2, 0, 0, 16382): normalised in float, w becomes exactly 1, so
        // floor/ceiling rounding alone cannot get under unit length. The clip must still not overshoot.
        var skeleton = Skeleton.FromFile(SampleMesh());
        var idle = SampleClips.Idle();
        var key = new RfaRotKey(idle.StartTime, -2, 0, 0, 16382);
        var odd = idle with { Bones = idle.Bones.SetItem(0, idle.Bones[0] with { RotationKeys = [key, key with { Time = idle.EndTime }] }) };
        var options = new NewClipOptions { PoseClip = odd, PoseTime = odd.StartTime };
        var clip = NewClip.Create(skeleton, options);
        AssertWellFormed(clip, skeleton, options);
        Assert.True(Quat.AngleDegrees(ClipSampler.KeyRotation(clip.Bones[0].RotationKeys[0]), ClipSampler.KeyRotation(key)) < 0.001f);
    }

    [Theory]
    [InlineData(NewClipKind.State, 0, 0)]
    [InlineData(NewClipKind.Action, 480, 480)]
    public void KindsGiveTheirRamps(NewClipKind kind, int rampIn, int rampOut)
    {
        var options = NewClipOptions.ForKind(kind);
        Assert.Equal((rampIn, rampOut), (options.RampIn, options.RampOut));
        var clip = NewClip.Create(Skeleton.FromFile(SampleMesh()), options);
        Assert.Equal((rampIn, rampOut), (clip.RampIn, clip.RampOut));
        Assert.Equal(NewClipOptions.DefaultLength, clip.Duration);
    }

    [Fact]
    public void ImpossibleOptionsAreRefusedInPlainWords()
    {
        var skeleton = Skeleton.FromFile(SampleMesh());
        var refusals = new (string Expect, NewClipOptions Options, Skeleton Skeleton)[]
        {
            ("no bones", new NewClipOptions(), Skeleton.Empty),
            ("at least one tick", new NewClipOptions { Length = 0 }, skeleton),
            ("negative", new NewClipOptions { StartTime = -1 }, skeleton),
            ("7 or 8", new NewClipOptions { Version = 6 }, skeleton),
            ("between 0 and 10", new NewClipOptions { Weight = 11 }, skeleton),
            ("between 0 and 10", new NewClipOptions { Weight = float.NaN }, skeleton),
            ("negative", new NewClipOptions { RampIn = -1 }, skeleton),
            ("longer than the clip", new NewClipOptions { Length = 400, RampIn = 300, RampOut = 300 }, skeleton),
            ("by index", new NewClipOptions { PoseClip = new RfaClip { Bones = [] } }, skeleton),
        };
        foreach (var (expect, options, s) in refusals)
        {
            string? problem = NewClip.Problem(s, options);
            Assert.NotNull(problem);
            Assert.Contains(expect, problem, StringComparison.Ordinal);
            var ex = Assert.Throws<ArgumentException>(() => NewClip.Create(s, options));
            Assert.Contains(expect, ex.Message, StringComparison.Ordinal);
        }
        Assert.Null(NewClip.Problem(skeleton, new NewClipOptions { Length = 1, StartTime = 0 }));
    }

    [Fact]
    public void TheSampleFigureHasNoSuggestedPoseClip()
    {
        // Its clips are in no table and it matches no built-in rig: it starts from its bind pose.
        var skeleton = Skeleton.FromFile(SampleMesh());
        Assert.Null(NewClip.SuggestPoseClip(SampleSet.MeshFile, skeleton, ClipUsageIndex.Empty, _ => true));
        Assert.Null(NewClip.SuggestPoseClip(SampleSet.MeshFile, skeleton, null, _ => true));
    }

    // ── Stock data (read in place; passes trivially without it) ────────────────

    private static (V3dFile Mesh, RfaClip Stand)? Guard()
    {
        if (TestPaths.CorpusFile("ult2_guard.v3c") is not { } meshPath || TestPaths.CorpusFile("ult2_stand.rfa") is not { } standPath) return null;
        return (V3dReader.ReadFile(meshPath), RfaReader.ReadFile(standPath));
    }

    private static ClipUsageIndex? StockTables()
    {
        if (TestPaths.Tables is not { } folder) return null;
        string? Text(string name) => File.Exists(Path.Combine(folder, name)) ? File.ReadAllText(Path.Combine(folder, name)) : null;
        return ClipUsageIndex.FromTexts(Text("entity.tbl"), Text("weapons.tbl"), Text("pc_multi.tbl"), Text("fpgun.tbl"));
    }

    [Fact]
    public void AStockCharacterStartsFromItsTableStandClipWithItsBoneLengths()
    {
        if (Guard() is not var (mesh, stand) || StockTables() is not { } usage)
        {
            output.WriteLine("corpus or tables absent; skipped");
            return;
        }
        var skeleton = Skeleton.FromFile(mesh);
        var suggestion = NewClip.SuggestPoseClip("ult2_guard.v3c", skeleton, usage, name => TestPaths.CorpusFile(name) is not null);
        Assert.NotNull(suggestion);
        Assert.Equal("ult2_stand.rfa", suggestion.ClipName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(NewClipPoseReason.TableStand, suggestion.Reason);
        Assert.Equal("guard1", suggestion.ClassName, StringComparer.OrdinalIgnoreCase);

        // A re-skinned mesh on the same skeleton that no table names gets the rig profile's stand clip.
        var rig = NewClip.SuggestPoseClip("my_reskin.v3c", skeleton, usage, name => TestPaths.CorpusFile(name) is not null);
        Assert.NotNull(rig);
        Assert.Equal(NewClipPoseReason.RigProfile, rig.Reason);
        Assert.Equal("ult2_stand.rfa", rig.ClipName, StringComparer.OrdinalIgnoreCase);

        foreach (var kind in new[] { NewClipKind.State, NewClipKind.Action })
        {
            var options = NewClipOptions.ForKind(kind) with { PoseClip = stand, PoseTime = stand.StartTime };
            var clip = NewClip.Create(mesh, options);
            AssertWellFormed(clip, skeleton, options);
            AssertHolds(clip, b => (ClipSampler.SampleRotation(stand.Bones[b].RotationKeys.AsSpan(), stand.StartTime),
                ClipSampler.SamplePosition(stand.Bones[b].PositionKeys.AsSpan(), stand.StartTime)));
            // The bone lengths every clip of the character carries.
            for (int b = 0; b < clip.BoneCount; b++)
                Assert.True(Vector3.Distance(clip.Bones[b].PositionKeys[0].Position, stand.Bones[b].PositionKeys[0].Position) < 1e-6f || skeleton.EffectiveParents[b] < 0);
            var diagnostics = ClipLinter.Analyze(clip, Context(mesh, "ult2_guard.v3c", "ult2_guard_new.rfa", stand, "ult2_stand.rfa"));
            Assert.True(diagnostics.Count == 0, Describe(diagnostics));
            AssertRoundTrips(clip);
        }
    }

    [Fact]
    public void ABindPoseClipOnAStockCharacterCarriesTheBindLengthsNotTheStandClips()
    {
        if (Guard() is not var (mesh, stand))
        {
            output.WriteLine("corpus absent; skipped");
            return;
        }
        var clip = NewClip.Create(mesh, new NewClipOptions());
        var diagnostics = ClipLinter.Analyze(clip, Context(mesh, "ult2_guard.v3c", "ult2_guard_new.rfa"));
        Assert.True(diagnostics.Count == 0, Describe(diagnostics));
        // Against the stand clip the bind's lengths differ (why the stand clip is the better start).
        var against = ClipLinter.Analyze(clip, Context(mesh, "ult2_guard.v3c", "ult2_guard_new.rfa", stand, "ult2_stand.rfa"));
        output.WriteLine(Describe(against));
        Assert.All(against, d => Assert.Equal(ClipRules.ProportionsDiffer, d.Code));
    }

    [Fact]
    public void EveryStockCharacterGetsALintCleanBindPoseClip()
    {
        if (TestPaths.Corpus is not { } corpus)
        {
            output.WriteLine("corpus absent; skipped");
            return;
        }
        int meshes = 0;
        foreach (string path in Directory.EnumerateFiles(corpus, "*.v3c"))
        {
            var mesh = V3dReader.ReadFile(path);
            var skeleton = Skeleton.FromFile(mesh);
            if (skeleton.Count == 0) continue;
            var options = NewClipOptions.ForKind(NewClipKind.Action);
            var clip = NewClip.Create(mesh, options);
            AssertWellFormed(clip, skeleton, options);
            var diagnostics = ClipLinter.Analyze(clip, Context(mesh, Path.GetFileName(path), "new_clip.rfa"));
            Assert.True(diagnostics.Count == 0, $"{Path.GetFileName(path)}: {Describe(diagnostics)}");
            AssertRoundTrips(clip);
            meshes++;
        }
        output.WriteLine($"{meshes} stock characters");
        Assert.True(meshes > 0);
    }
}
