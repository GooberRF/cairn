using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Retarget;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Retarget tests on the stock corpus (read in place; they pass trivially when it is absent):
/// the method self-tests of verify.py, the built-in profiles against the stock meshes, the
/// automatic bone maps of the four stock rigs, and the generic profile on a non-humanoid.
/// </summary>
public class RetargetCorpusTests(ITestOutputHelper output)
{
    public static TheoryData<string> SourceClips()
    {
        var data = new TheoryData<string>();
        foreach (var (source, _) in RetargetCorpus.SourceClips) data.Add(source);
        return data;
    }

    // ── self-tests (verify.py) ───────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(SourceClips))]
    public void IdentityAndRoundTripThroughTheFemaleRigReturnTheSourceRotations(string sourceName)
    {
        var clip = RetargetCorpus.Clip(sourceName);
        var a = RetargetCorpus.Rig(RigProfiles.RigA);
        var female = RetargetCorpus.Rig(RigProfiles.Female);
        if (clip is null || a is null || female is null) return;
        var options = RetargetOptions.PureFrameOffset;

        var ident = Retargeter.Retarget(new RetargetRequest(clip, a, a) { Options = options });
        Assert.True(ident.Success, ident.Error);
        var there = Retargeter.Retarget(new RetargetRequest(clip, a, female) { Options = options });
        Assert.True(there.Success, there.Error);
        var back = Retargeter.Retarget(new RetargetRequest(there.Clip!, female, a) { Options = options });
        Assert.True(back.Success, back.Error);

        double d0 = ClipCompare.MaxRotationDegrees(clip, ident.Clip!, engine: false);
        double d0e = ClipCompare.MaxRotationDegrees(clip, ident.Clip!, engine: true);
        double d1 = ClipCompare.MaxRotationDegrees(clip, back.Clip!, engine: false);
        double d1e = ClipCompare.MaxRotationDegrees(clip, back.Clip!, engine: true);
        output.WriteLine($"self-test {sourceName}: identity max {d0:0.0000} deg (reference sampler) / {d0e:0.0000} deg (engine); "
            + $"A->female->A max {d1:0.0000} deg / {d1e:0.0000} deg (rotation transfer only)");
        Assert.True(d0 <= 0.01, $"identity {d0} deg");
        Assert.True(d1 <= 0.01, $"A->female->A {d1} deg");
        Assert.True(d0e <= 0.05 && d1e <= 0.05, $"engine sampler {d0e} / {d1e} deg");
    }

    [Theory]
    [MemberData(nameof(SourceClips))]
    public void WithoutIkEveryAlignedSegmentFollowsTheSource(string sourceName)
    {
        var clip = RetargetCorpus.Clip(sourceName);
        var a = RetargetCorpus.Rig(RigProfiles.RigA);
        if (clip is null || a is null) return;
        foreach (var profile in new[] { RigProfiles.Female, RigProfiles.Merc, RigProfiles.Civilian })
        {
            var target = RetargetCorpus.Rig(profile)!;
            var result = Retargeter.Retarget(new RetargetRequest(clip, a, target) { Options = RetargetOptions.Default with { Ik = false } });
            Assert.True(result.Success, result.Error);
            var reference = RetargetReport.Build(clip, a.Skeleton, a.Profile, result.Clip!, target.Skeleton, target.Profile, result.BoneMap!, sampler: ReportSampler.Reference);
            var engine = RetargetReport.Build(clip, a.Skeleton, a.Profile, result.Clip!, target.Skeleton, target.Profile, result.BoneMap!);
            double worst = reference.WorstSegmentDirection!.SegmentDirectionMaxDegrees!.Value;
            double worstEngine = engine.WorstSegmentDirection!.SegmentDirectionMaxDegrees!.Value;
            output.WriteLine($"{profile.Name} {sourceName} (IK off): worst segment direction {worst:0.000} deg ({reference.WorstSegmentDirection.CanonicalName}), engine sampler {worstEngine:0.000} deg");
            // validation.md: before IK every limb segment matches the source within 0.02 degrees.
            Assert.True(worst <= 0.02, $"{profile.Name}: {worst} deg at {reference.WorstSegmentDirection.CanonicalName}");
            Assert.True(engine.StructureOk);
        }
    }

    [Fact]
    public void SingleSkeletonRestStaysWithinOneQuantisationStepOfTheGoldens()
    {
        // Without the stored bind rotations the retargeter uses Skeleton's single-precision rest
        // (10 of 25 rig A rest quaternions change in the last float bit when normalised in single
        // precision); measure what that costs against the goldens. Each golden is compared with the
        // quantisation of its own generation (retarget.py's rounding or RFA Workbench's default), found by
        // the full rig reproducing it byte for byte.
        var a = RetargetCorpus.Rig(RigProfiles.RigA);
        if (a is null || RetargetCorpus.Goldens.Count == 0) return;
        var source = new RetargetRig(a.Skeleton, a.Profile) { ReferenceClip = a.ReferenceClip };
        int worstUnits = 0, components = 0;
        double worstDeg = 0;
        foreach (var (sourceName, clipName) in RetargetCorpus.SourceClips)
        {
            var clip = RetargetCorpus.Clip(sourceName)!;
            foreach (var profile in new[] { RigProfiles.Female, RigProfiles.Merc, RigProfiles.Civilian })
            {
                string name = $"af_{profile.Name}_{clipName}.rfa";
                var full = RetargetCorpus.Rig(profile)!;
                var target = new RetargetRig(full.Skeleton, profile) { ReferenceClip = full.ReferenceClip };
                foreach (string golden in RetargetCorpus.Golden(name))
                {
                    string label = $"{Path.GetFileName(Path.GetDirectoryName(golden))}/{name}";
                    var generations = RetargetCorpus.GenerationsOf(golden, clip, a, full);
                    Assert.True(generations.Length > 0, $"{label}: the full rig reproduces it in neither generation");
                    var quantization = generations[0];
                    var result = Retargeter.Retarget(new RetargetRequest(clip, source, target) { Options = RetargetCorpus.Seated(quantization) });
                    Assert.True(result.Success, result.Error);
                    var expected = RfaReader.ReadFile(golden);
                    var diff = ClipCompare.Bytes(result.Clip!, expected);
                    double rot = ClipCompare.MaxRotationDegrees(result.Clip!, expected, engine: false);
                    output.WriteLine($"float rest {label} ({RetargetCorpus.Describe(quantization)}): {diff.RotComponents} rotation components differ (max {diff.MaxRotUnits} units), "
                        + $"{diff.PosFloats} position floats (max {diff.MaxPosUlps} ulps), max rotation {rot:0.0000} deg");
                    worstUnits = Math.Max(worstUnits, diff.MaxRotUnits);
                    components += diff.RotComponents;
                    worstDeg = Math.Max(worstDeg, rot);
                }
            }
        }
        output.WriteLine($"float rest overall: {components} components, max {worstUnits} units, {worstDeg:0.0000} deg");
        Assert.True(worstUnits <= 2, $"{worstUnits} units");
        Assert.True(worstDeg < 0.01, $"{worstDeg} deg");
    }

    // ── profiles and maps on the stock meshes ────────────────────────────────

    [Fact]
    public void BuiltInProfilesRecogniseEveryMeshOfTheirRig()
    {
        if (TestPaths.Corpus is null) return;
        foreach (var profile in RigProfiles.BuiltIn)
        {
            foreach (string mesh in profile.MeshNames)
            {
                var file = RetargetCorpus.Mesh(mesh);
                if (file is null) continue;
                var found = RigProfiles.FindFor(Skeleton.FromFile(file));
                Assert.True(ReferenceEquals(profile, found), $"{mesh}: expected {profile.Name}, got {found?.Name ?? "none"}");
            }
        }
        Assert.Null(RigProfiles.FindFor(Skeleton.FromFile(RetargetCorpus.Mesh("bat1.v3c")!)));
    }

    [Fact]
    public void StockRigsAutoMapWithTheExpectedStatuses()
    {
        var a = RetargetCorpus.Rig(RigProfiles.RigA);
        var female = RetargetCorpus.Rig(RigProfiles.Female);
        var merc = RetargetCorpus.Rig(RigProfiles.Merc);
        var civilian = RetargetCorpus.Rig(RigProfiles.Civilian);
        if (a is null || female is null || merc is null || civilian is null) return;

        BoneMap Map(RetargetRig s, RetargetRig t) => BoneMapper.Map(s.Skeleton, s.Profile, t.Skeleton, t.Profile);
        BoneMapEntry Entry(BoneMap m, RetargetRig t, string canonical) => m.Entries[t.Profile.IndexOf(t.Skeleton.Names, canonical)];

        var toFemale = Map(a, female);
        Assert.All(toFemale.Entries, e => Assert.Equal(BoneMapStatus.Mapped, e.Status));
        Assert.All(toFemale.Entries, e => Assert.Equal(BoneMatchKind.ExactName, e.Match));
        // Female indices 1-3 are fingers-r, fingers-r2, fingers-l2: mapped by name, not index.
        Assert.Equal([2, 3, 1], toFemale.Entries.Skip(1).Take(3).Select(e => e.SourceIndex));
        Assert.Empty(toFemale.UnusedSourceBones);
        Assert.Empty(toFemale.Validate());

        var toMerc = Map(a, merc);
        Assert.Equal(BoneMapStatus.Unmapped, Entry(toMerc, merc, "shoulderpad-l").Status);
        Assert.Equal(BoneMapStatus.Unmapped, Entry(toMerc, merc, "shoulderpad-r").Status);
        Assert.Equal(BoneMapStatus.Reparented, Entry(toMerc, merc, "spine03").Status);
        Assert.Equal(24, toMerc.Count(BoneMapStatus.Mapped));
        Assert.All(toMerc.Entries.Where(e => e.IsMapped), e => Assert.Equal(BoneMatchKind.CanonicalName, e.Match));
        Assert.Empty(toMerc.UnusedSourceBones);
        Assert.DoesNotContain(toMerc.Validate(), p => p.Severity == BoneMapSeverity.Error);
        Assert.Contains(toMerc.Validate(), p => p.Severity == BoneMapSeverity.Warning && p.Message.Contains("spine03"));

        var toCivilian = Map(a, civilian);
        Assert.Equal(BoneMapStatus.Unmapped, Entry(toCivilian, civilian, "thumb-l2").Status);
        Assert.Equal(BoneMapStatus.Unmapped, Entry(toCivilian, civilian, "thumb-r2").Status);
        Assert.Equal(25, toCivilian.Count(BoneMapStatus.Mapped));
        Assert.Equal("ult2-bdbn-upperarm-l", Entry(toCivilian, civilian, "upperarm-l").SourceName, ignoreCase: true);
        Assert.Empty(toCivilian.UnusedSourceBones);

        // The other way round: the merc's shoulderpads have no target, and rig A's spine03 hangs off
        // pelvis while the merc's hangs off root (not an ancestor of pelvis): cross-branch.
        var fromMerc = Map(merc, a);
        Assert.Equal(["mrc1-bdbn-shoulderpad-l", "mrc1-bdbn-shoulderpad-r"], fromMerc.UnusedSourceBones.Select(b => b.Name.ToLowerInvariant()));
        Assert.Equal(BoneMapStatus.CrossBranch, Entry(fromMerc, a, "spine03").Status);
    }

    [Fact]
    public void GenericProfileOfANonHumanoidHasNoIkChains()
    {
        var bat = RetargetCorpus.Mesh("bat1.v3c");
        var guard = RetargetCorpus.Mesh("ult2_guard.v3c");
        var tech = RetargetCorpus.Mesh("tech01.v3c");
        if (bat is null || guard is null || tech is null) return;

        var batProfile = RigProfile.Generic(Skeleton.FromFile(bat));
        output.WriteLine($"bat1: {string.Join(", ", Skeleton.FromFile(bat).Names)}; root {batProfile.RootBone}, pelvis {batProfile.PelvisBone ?? "none"}");
        Assert.Empty(batProfile.IkChains);
        Assert.False(string.IsNullOrEmpty(batProfile.RootBone));

        // On a stock humanoid the generated profile finds what the built-in states.
        var g = RigProfile.Generic(Skeleton.FromFile(guard));
        Assert.Equal(RigProfiles.StockIkChains.Select(x => (x.Upper, x.Lower, x.End, x.PoleBias)), g.IkChains.Select(x => (x.Upper, x.Lower, x.End, x.PoleBias)));
        Assert.Equal("pelvis", g.PelvisBone);
        Assert.Equal("root", g.RootBone);
        Assert.Equal(RigProfiles.StockPrimaryChildren.OrderBy(kv => kv.Key), g.PrimaryChildren.OrderBy(kv => kv.Key));

        var c = RigProfile.Generic(Skeleton.FromFile(tech));
        Assert.Equal(string.Join(",", RigProfiles.StockIkChains.Select(x => x.Upper).Order()), string.Join(",", c.IkChains.Select(x => x.Upper).Order()));
        Assert.Equal("upperarm-l", c.Canonical("tech- arm-l-upper"));
    }
}
