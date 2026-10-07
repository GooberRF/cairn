using System.Numerics;
using System.Text.Json.Nodes;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Tbl;
using Cairn.Rfa.Retarget;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Phase 7a: the retarget presets, the hip-height root, pinned contacts and the off-hand grip, on the
/// synthetic rig and (corpus-optional, read in place) on the four stock humanoid rigs.
/// </summary>
public class RetargetPresetTests(ITestOutputHelper output)
{
    private static RetargetRig Source => SyntheticRig.Rig("srcx-bdbn-");

    private static RetargetRig Target => SyntheticRig.Rig("tgtx-bdbn-", reversed: true, twistFrames: true, scale: 1.2f);

    // ── presets ──────────────────────────────────────────────────────────────

    [Fact]
    public void PresetsSetTheirOptionsAndAreRecognised()
    {
        var seated = RetargetPresets.Options(RetargetPreset.Seated);
        Assert.Equal(RootMode.AnchorPelvis, seated.RootMode);
        Assert.True(seated.Ik && seated.IkArms && seated.IkLegs);
        Assert.Equal(RetargetPreset.Seated, RetargetPresets.Identify(RetargetOptions.Default));
        // The output encoding is not part of a preset.
        Assert.Equal(RetargetPreset.Seated, RetargetPresets.Identify(RetargetOptions.Reference));
        Assert.Equal(KeyQuantization.Reference, RetargetPresets.Options(RetargetPreset.Seated, RetargetOptions.Reference).Quantization);
        Assert.Equal(RetargetPreset.Seated, RetargetPresets.Identify(RetargetOptions.Default with { ResampleStep = 160 }));

        var loco = RetargetPresets.Options(RetargetPreset.Locomotion);
        Assert.Equal(RootMode.HipHeight, loco.RootMode);
        Assert.True(loco.Ik && loco.IkLegs && !loco.IkArms);
        Assert.Equal(RetargetPreset.Locomotion, RetargetPresets.Identify(loco));

        var rot = RetargetPresets.Options(RetargetPreset.RotationOnly);
        Assert.Equal(RootMode.HipHeight, rot.RootMode);
        Assert.False(rot.Ik);
        Assert.Equal(RetargetPreset.RotationOnly, RetargetPresets.Identify(rot));
        // IK details do not matter while IK is off.
        Assert.Equal(RetargetPreset.RotationOnly, RetargetPresets.Identify(rot with { IkArms = false, DisabledIkChains = ["upperleg-l"] }));

        Assert.Equal(RetargetPreset.Custom, RetargetPresets.Identify(seated with { RootMode = RootMode.CopySource }));
        // The two-handed grip is on by default with standing / locomotion only (phase 7b)...
        Assert.True(loco.OffHandFollowsMainHand && RetargetPresets.GripByDefault(RetargetPreset.Locomotion));
        Assert.False(seated.OffHandFollowsMainHand || rot.OffHandFollowsMainHand);
        Assert.False(RetargetPresets.Options(RetargetPreset.Seated, RetargetOptions.Reference).OffHandFollowsMainHand);
        // ...goes with any preset (not part of its identity), and applying a preset with options to keep keeps it.
        Assert.Equal(RetargetPreset.Locomotion, RetargetPresets.Identify(loco with { OffHandFollowsMainHand = false }));
        Assert.True(RetargetPresets.Options(RetargetPreset.Seated, loco with { OffHandFollowsMainHand = true }).OffHandFollowsMainHand);
        Assert.False(RetargetPresets.Options(RetargetPreset.Locomotion, seated).OffHandFollowsMainHand);
        Assert.Equal(RetargetPreset.Custom, RetargetPresets.Identify(loco with { ScaleStride = true }));
        Assert.Equal(RetargetPreset.Custom, RetargetPresets.Identify(seated with { DisabledIkChains = ["upperarm-l"] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetargetPresets.Options(RetargetPreset.Custom));
        Assert.All(RetargetPresets.All, p => Assert.False(string.IsNullOrWhiteSpace(RetargetPresets.Description(p))));
    }

    [Fact]
    public void SuggestionsFollowTheTablesThenTheName()
    {
        // No tables: the name decides.
        Assert.Equal(RetargetPreset.Seated, RetargetPresets.Suggest("park_jeep_driver.rfa", null).Preset);
        Assert.Equal(RetargetPreset.Seated, RetargetPresets.Suggest("tech01_seated_idle", null).Preset);
        Assert.Equal(RetargetPreset.Seated, RetargetPresets.Suggest("AdminMaleSitting", null).Preset);
        Assert.Equal(RetargetPreset.RotationOnly, RetargetPresets.Suggest("park_swim_walk", null).Preset);
        Assert.Equal(RetargetPreset.Locomotion, RetargetPresets.Suggest("ult2_walk", null).Preset);
        Assert.Equal(RetargetPreset.Locomotion, RetargetPresets.Suggest("situp", null).Preset); // a word, not a substring

        // Tables win over the name.
        const string entity = """
            $Name: "rider"
            $V3D Filename: "rider.vcm"
            +State: "jeep_drive" "plain_clip.mvf"
            +State: "walk" "my_jeep_walk.mvf"
            +State: "swim_walk" "paddle.mvf"
            +Action: "fire_stand" "rider_fire.mvf"
            """;
        var usage = ClipUsageIndex.FromTexts(entity, null, null, null);
        var s = RetargetPresets.Suggest("plain_clip", usage);
        Assert.Equal(RetargetPreset.Seated, s.Preset);
        Assert.Contains("jeep_drive", s.Reason);
        Assert.Equal(RetargetPreset.Locomotion, RetargetPresets.Suggest("my_jeep_walk", usage).Preset);
        Assert.Equal(RetargetPreset.RotationOnly, RetargetPresets.Suggest("paddle", usage).Preset);
        Assert.Equal(RetargetPreset.Locomotion, RetargetPresets.Suggest("rider_fire", usage).Preset);
    }

    [Fact]
    public void RetiredScaleByLegLengthMigratesToHipHeight()
    {
        var options = JsonNode.Parse("""{ "RestAlignment": true, "RootMode": "ScaleByLegLength" }""")!.AsObject();
        string? note = RetargetPresets.MigrateOptionsJson(options);
        Assert.NotNull(note);
        Assert.Equal("HipHeight", (string?)options["RootMode"]);
        Assert.Null(RetargetPresets.MigrateOptionsJson(options));
        var camel = JsonNode.Parse("""{ "rootMode": "scalebyleglength" }""")!.AsObject();
        Assert.NotNull(RetargetPresets.MigrateOptionsJson(camel));
        Assert.Equal("HipHeight", (string?)camel["rootMode"]);
    }

    // ── hip height on the synthetic rig ──────────────────────────────────────

    [Fact]
    public void HipHeightStandsTheHipsAboveTheTargetsGroundAndHoldsTheFeet()
    {
        var src = Source;
        var tgt = Target;
        var clip = SyntheticRig.Motion(src);
        var result = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { Options = RetargetPresets.Options(RetargetPreset.Locomotion) });
        Assert.True(result.Success, result.Error);
        var g = result.Ground!;
        output.WriteLine(g.ToString());
        foreach (string line in result.ReportLines) output.WriteLine("  " + line);
        // Both rigs' grounds come from their reference clips (the rest pose: ankles at 0.08 m x scale).
        Assert.Equal(0.08, g.SourceGround, 4);
        Assert.Equal(0.096, g.TargetGround, 4);
        Assert.True(g.LegRatio is > 1.1 and <= 1.2 + 1e-6, $"{g.LegRatio}");
        Assert.Equal(1.0, g.HorizontalScale);

        // Only the legs are held, to the ground, and they reach.
        Assert.Equal(2, result.Contacts.Length);
        Assert.All(result.Contacts, c =>
        {
            Assert.True(c.IsLeg);
            Assert.Equal("the ground contact", c.HeldTo);
            Assert.False(c.Stretched, c.StretchNote);
            Assert.True(c.MaxErrorCm < 0.5, $"{c.EndBone} {c.MaxErrorCm} cm");
        });
        // The main arm follows the source's rotations; the off arm is only the two-handed grip (on by default
        // with this preset), which never holds here because the synthetic rig's hands stay apart.
        Assert.Contains(result.ReportLines, l => l.StartsWith("IK upperarm-r off", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Contacts, c => !c.IsLeg);

        // At the first key (the rest pose) the target's hips stand at the ratio-scaled height.
        var pose = new Pose(tgt.Skeleton);
        pose.Sample(result.Clip!, 160);
        int hl = tgt.Profile.IndexOf(tgt.Skeleton.Names, "upperleg-l"), hr = tgt.Profile.IndexOf(tgt.Skeleton.Names, "upperleg-r");
        float hip = (pose.World[hl].Position.Y + pose.World[hr].Position.Y) / 2;
        Assert.Equal(g.TargetGround + (0.95 - g.SourceGround) * g.LegRatio, hip, 3);

        // Rotation only: the same root, no IK at all.
        var rot = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { Options = RetargetPresets.Options(RetargetPreset.RotationOnly) });
        Assert.True(rot.Success, rot.Error);
        Assert.Empty(rot.Contacts);
        int root = tgt.Profile.IndexOf(tgt.Skeleton.Names, "root");
        Assert.Equal(result.Clip!.Bones[root].PositionKeys.ToArray(), rot.Clip!.Bones[root].PositionKeys.ToArray());
    }

    [Fact]
    public void HipHeightNeedsLegChainsAndSaysSo()
    {
        var src = Source;
        var tgt = Target with { Profile = Target.Profile with { IkChains = [] } };
        var result = Retargeter.Retarget(new RetargetRequest(SyntheticRig.Motion(src), src, tgt) { Options = RetargetPresets.Options(RetargetPreset.RotationOnly) });
        Assert.False(result.Success);
        Assert.Contains("hip height", result.Error);
        Assert.Contains("leg IK chain", result.Error);
    }

    [Fact]
    public void WithoutAReferenceClipTheGroundFallsBackWithAWarning()
    {
        var src = Source with { ReferenceClip = null };
        var result = Retargeter.Retarget(new RetargetRequest(SyntheticRig.Motion(src), src, Target) { Options = RetargetPresets.Options(RetargetPreset.Locomotion) });
        Assert.True(result.Success, result.Error);
        Assert.Equal("the source clip itself", result.Ground!.SourceFrom);
        Assert.Contains(result.Warnings, w => w.Contains("ground was taken from the clip itself"));
    }

    // ── stock rigs (corpus-optional) ─────────────────────────────────────────

    public static TheoryData<string, string> StandingCases()
    {
        var data = new TheoryData<string, string>();
        foreach (string rig in new[] { "A", "female", "merc", "civilian" })
        {
            foreach (string clip in new[] { "ult2_stand", "ult2_walk", "park_run", "ult2_run", "ult2_crouch" }) data.Add(rig, clip);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(StandingCases))]
    public void LocomotionKeepsStockFeetOnTheGroundWithoutStretchedLegs(string rig, string clipName)
    {
        var a = RetargetCorpus.Rig(RigProfiles.RigA);
        var target = RetargetCorpus.Rig(RigProfiles.Get(rig)!);
        var clip = RetargetCorpus.Clip(clipName + ".rfa");
        if (a is null || target is null || clip is null) return;

        var result = Retargeter.Retarget(new RetargetRequest(clip, a, target) { Options = RetargetPresets.Options(RetargetPreset.Locomotion) });
        Assert.True(result.Success, result.Error);
        var legs = result.Contacts.Where(c => c.IsLeg).ToList();
        Assert.Equal(2, legs.Count);
        var g = result.Ground!;
        var (soleMin, soleMean) = Soles(target, result.Clip!, g.TargetGround);
        var (sourceMin, _) = Soles(a, clip, g.SourceGround);
        output.WriteLine($"{rig} {clipName}: ratio {g.LegRatio:0.000}; legs " + string.Join(", ", legs.Select(c => $"{c.EndBone} {c.MaxErrorCm:0.00} cm, stretched {c.StretchedSamples}/{c.Samples}"))
            + $"; lowest foot point {soleMin * 100:0.0} cm (mean {soleMean * 100:0.0} cm) from the ground; the source's {sourceMin * 100:0.0} cm from its own");
        Assert.All(legs, c => Assert.False(c.Stretched, $"{c.EndBone}: {c.StretchNote}"));
        Assert.All(legs, c => Assert.True(c.MaxErrorCm < 0.5, $"{c.EndBone} {c.MaxErrorCm} cm"));
        if (clipName is "ult2_stand" or "ult2_crouch")
        {
            // Planted feet: the lowest foot point sits on the target's ground.
            Assert.True(Math.Abs(soleMin) < 0.005, $"{soleMin * 100:0.00} cm");
        }
        // The feet come as close to the target's ground as the source's to its own (the stock walks and
        // runs dip 2-4 cm below their stand's ground), scaled by the leg ratio.
        Assert.True(Math.Abs(soleMin - sourceMin * g.LegRatio) < 0.01, $"{soleMin * 100:0.00} cm against the source's {sourceMin * 100:0.00} cm");
    }

    [Theory]
    [InlineData("female")]
    [InlineData("merc")]
    [InlineData("civilian")]
    public void LocomotionStandsWhereTheSeatedMethodFloatsOrStretches(string rig)
    {
        var a = RetargetCorpus.Rig(RigProfiles.RigA);
        var target = RetargetCorpus.Rig(RigProfiles.Get(rig)!);
        var stand = RetargetCorpus.Clip("ult2_stand.rfa");
        if (a is null || target is null || stand is null) return;
        var loco = Retargeter.Retarget(new RetargetRequest(stand, a, target) { Options = RetargetPresets.Options(RetargetPreset.Locomotion) });
        var seated = Retargeter.Retarget(new RetargetRequest(stand, a, target) { Options = RetargetPresets.Options(RetargetPreset.Seated) });
        double ground = loco.Ground!.TargetGround;
        var (seatedSole, _) = Soles(target, seated.Clip!, ground);
        var (locoSole, _) = Soles(target, loco.Clip!, ground);
        bool seatedStretched = seated.Contacts.Any(c => c.IsLeg && c.Stretched);
        output.WriteLine($"{rig} ult2_stand: seated feet {seatedSole * 100:0.0} cm above the ground (legs stretched: {seatedStretched}); locomotion {locoSole * 100:0.0} cm");
        // Seated keeps rig A's hip and foot heights, which float the target 8-13 cm above its own ground.
        Assert.True(seatedSole > 0.05, $"{seatedSole}");
        Assert.True(Math.Abs(locoSole) < 0.005, $"{locoSole}");
    }

    [Theory]
    [InlineData("park_jeep_driver.rfa")]
    [InlineData("park_jeep_gunner.rfa")]
    [InlineData("ult2_on_turret.rfa")]
    public void SeatedIsStillTheRightPresetForSeatedClips(string clipName)
    {
        var a = RetargetCorpus.Rig(RigProfiles.RigA);
        var clip = RetargetCorpus.Clip(clipName);
        if (a is null || clip is null) return;
        foreach (var profile in new[] { RigProfiles.Female, RigProfiles.Merc, RigProfiles.Civilian })
        {
            var target = RetargetCorpus.Rig(profile)!;
            double Hands(RetargetPreset preset)
            {
                var r = Retargeter.Retarget(new RetargetRequest(clip, a, target) { Options = RetargetPresets.Options(preset) });
                var report = RetargetReport.Build(clip, a.Skeleton, a.Profile, r.Clip!, target.Skeleton, target.Profile, r.BoneMap!, contacts: r.Contacts);
                return Math.Max(report.HandsModelSpaceMaxCm, report.FeetModelSpaceMaxCm);
            }
            double seated = Hands(RetargetPreset.Seated), loco = Hands(RetargetPreset.Locomotion);
            output.WriteLine($"{profile.Name} {clipName}: hands/feet from the source's controls, seated {seated:0.0} cm, locomotion {loco:0.0} cm");
            Assert.True(seated < 0.5, $"{seated}");
            Assert.True(loco > 5.0, $"{loco}");
        }
    }

    [Fact]
    public void LocomotionOntoTheSameRigChangesAlmostNothing()
    {
        var a = RetargetCorpus.Rig(RigProfiles.RigA);
        var walk = RetargetCorpus.Clip("ult2_walk.rfa");
        if (a is null || walk is null) return;
        // The clip's own bone lengths (ult2_walk's spine is 2 cm off ult2_stand's, which the default
        // reference-clip lengths would carry over).
        var options = RetargetPresets.Options(RetargetPreset.Locomotion) with { BoneLengths = BoneLengthSource.Source };
        var result = Retargeter.Retarget(new RetargetRequest(walk, a, a) { Options = options });
        Assert.True(result.Success, result.Error);
        double model = ClipCompare.MaxModelSpaceMetres(walk, result.Clip!, a.Skeleton);
        output.WriteLine($"A -> A ult2_walk (locomotion): ratio {result.Ground!.LegRatio:0.0000}, worst joint {model * 100:0.00} cm from the source");
        Assert.Equal(1.0, result.Ground.LegRatio, 3);
        Assert.True(model < 0.01, $"{model * 100} cm");
    }

    [Fact]
    public void TheOffHandStaysOnATwoHandedWeapon()
    {
        var a = RetargetCorpus.Rig(RigProfiles.RigA);
        var shotgun = RetargetCorpus.Clip("park_shotgun_walk.rfa");
        if (a is null || shotgun is null) return;
        foreach (var profile in new[] { RigProfiles.Female, RigProfiles.Merc, RigProfiles.Civilian })
        {
            var target = RetargetCorpus.Rig(profile)!;
            var plain = Retargeter.Retarget(new RetargetRequest(shotgun, a, target) { Options = RetargetPresets.Options(RetargetPreset.Locomotion) with { OffHandFollowsMainHand = false } });
            // On by default with the standing preset.
            var held = Retargeter.Retarget(new RetargetRequest(shotgun, a, target) { Options = RetargetPresets.Options(RetargetPreset.Locomotion) });
            double Grip(RfaClip c) => GripChange(a, shotgun, target, c);
            var off = held.Contacts.Single(c => c.EndBone == "hand-l");
            output.WriteLine($"{profile.Name} park_shotgun_walk: grip moves {Grip(plain.Clip!):0.0} cm with the arms by rotation, {Grip(held.Clip!):0.0} cm with the off hand held ({off.HeldTo}, {off.MaxErrorCm:0.00} cm)");
            Assert.True(Grip(plain.Clip!) > 5.0);
            Assert.True(Grip(held.Clip!) < 0.5);
            Assert.False(off.Stretched);
            Assert.Equal("the main hand (two-handed grip)", off.HeldTo);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>The lowest foot point (ankle or toe joint) relative to <paramref name="ground"/>: minimum over the clip and mean of the per-sample minimum.</summary>
    private static (double Min, double Mean) Soles(RetargetRig rig, RfaClip clip, double ground)
    {
        var p = rig.Profile;
        var sk = rig.Skeleton;
        int[] feet = [.. new[] { "foot-l", "foot-r", "toes-l", "toes-r" }.Select(n => p.IndexOf(sk.Names, n))];
        var pose = new Pose(sk);
        double min = double.MaxValue, sum = 0;
        var times = RetargetReport.SampleTimes(clip, 160);
        foreach (int t in times)
        {
            pose.Sample(clip, t);
            double low = feet.Min(i => pose.World[i].Position.Y) - ground;
            min = Math.Min(min, low);
            sum += low;
        }
        return (min, sum / times.Count);
    }

    /// <summary>How far the right-to-left hand vector moves from the source's while the source's hands are within 45 cm, cm.</summary>
    private static double GripChange(RetargetRig source, RfaClip clip, RetargetRig target, RfaClip output)
    {
        int sl = source.Profile.IndexOf(source.Skeleton.Names, "hand-l"), sr = source.Profile.IndexOf(source.Skeleton.Names, "hand-r");
        int tl = target.Profile.IndexOf(target.Skeleton.Names, "hand-l"), tr = target.Profile.IndexOf(target.Skeleton.Names, "hand-r");
        var sp = new Pose(source.Skeleton);
        var tp = new Pose(target.Skeleton);
        double worst = 0;
        foreach (int t in RetargetReport.SampleTimes(clip, 160))
        {
            sp.Sample(clip, t);
            tp.Sample(output, t);
            Vector3 ds = sp.World[sl].Position - sp.World[sr].Position, dt = tp.World[tl].Position - tp.World[tr].Position;
            if (ds.Length() > 0.45f) continue;
            worst = Math.Max(worst, (ds - dt).Length() * 100);
        }
        return worst;
    }
}
