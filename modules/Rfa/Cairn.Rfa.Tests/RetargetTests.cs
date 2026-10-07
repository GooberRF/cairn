using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Tests;

/// <summary>A small synthetic humanoid (no stock assets) for the retarget tests.</summary>
internal static class SyntheticRig
{
    // name, parent, rest joint position (model space, metres) of the "source" proportions.
    private static readonly (string Name, string? Parent, Vector3 Position)[] Humanoid =
    [
        ("root", null, new(0f, 0f, 0f)),
        ("pelvis", "root", new(0f, 1.0f, 0f)),
        ("spine", "pelvis", new(0f, 1.25f, 0f)),
        ("head", "spine", new(0f, 1.6f, 0f)),
        ("upperarm-l", "spine", new(-0.2f, 1.5f, 0f)),
        ("lowerarm-l", "upperarm-l", new(-0.5f, 1.5f, 0f)),
        ("hand-l", "lowerarm-l", new(-0.78f, 1.5f, 0f)),
        ("upperarm-r", "spine", new(0.2f, 1.5f, 0f)),
        ("lowerarm-r", "upperarm-r", new(0.5f, 1.5f, 0f)),
        ("hand-r", "lowerarm-r", new(0.78f, 1.5f, 0f)),
        ("upperleg-l", "pelvis", new(-0.1f, 0.95f, 0f)),
        ("lowerleg-l", "upperleg-l", new(-0.1f, 0.52f, 0.02f)),
        ("foot-l", "lowerleg-l", new(-0.1f, 0.08f, 0f)),
        ("upperleg-r", "pelvis", new(0.1f, 0.95f, 0f)),
        ("lowerleg-r", "upperleg-r", new(0.1f, 0.52f, 0.02f)),
        ("foot-r", "lowerleg-r", new(0.1f, 0.08f, 0f)),
    ];

    public static int BoneCount => Humanoid.Length;

    /// <summary>
    /// A rig: bone names get <paramref name="prefix"/>, the bone order is reversed when asked,
    /// every bone frame is turned by its own rotation when <paramref name="twistFrames"/>, and the
    /// proportions are scaled. Its reference clip holds the rest locals.
    /// </summary>
    public static RetargetRig Rig(string prefix, bool reversed = false, bool twistFrames = false, float scale = 1f, Func<string, string>? rename = null)
    {
        int n = Humanoid.Length;
        var order = Enumerable.Range(0, n).ToArray();
        if (reversed) Array.Reverse(order);
        var at = new int[n];
        for (int i = 0; i < n; i++) at[order[i]] = i;
        var bones = new V3dBone[n];
        for (int i = 0; i < n; i++)
        {
            var (name, parent, pos) = Humanoid[order[i]];
            var rot = twistFrames ? Quat.FromAxisAngle(Vector3.Normalize(new Vector3(1, 2 + i, 3)), 0.4f + 0.37f * i) : Quaternion.Identity;
            var world = new Rigid(rot, pos * scale);
            int p = parent is null ? -1 : at[Array.FindIndex(Humanoid, b => b.Name == parent)];
            string stored = prefix + (rename?.Invoke(name) ?? name);
            bones[i] = new V3dBone(FixedString.FromText(stored, 24), world.Rotation, world.Inverse().Position, p);
        }
        var skeleton = Skeleton.FromBones(bones);
        var profile = RigProfile.Generic(skeleton);
        var stand = new RfaClip
        {
            StartTime = 160,
            EndTime = 1120,
            Bones = [.. Enumerable.Range(0, n).Select(i => new RfaBoneTrack(4f,
                [RfaRotKey.Quantize(160, Quat.Conj(skeleton.RestLocal[i].Rotation))],
                [RfaPosKey.Constant(160, skeleton.RestLocal[i].Position), RfaPosKey.Constant(1120, skeleton.RestLocal[i].Position)]))],
        };
        return new RetargetRig(skeleton, profile) { ReferenceClip = stand, ReferenceClipName = prefix + "stand.rfa", BindRotations = [.. bones.Select(b => b.Rotation)] };
    }

    /// <summary>A clip for <paramref name="rig"/>: arms, legs, spine and root move over three keys; the root travels.</summary>
    public static RfaClip Motion(RetargetRig rig, RfaMorph? morph = null)
    {
        var sk = rig.Skeleton;
        int[] times = [160, 640, 1120];
        var tracks = new RfaBoneTrack[sk.Count];
        for (int i = 0; i < sk.Count; i++)
        {
            string name = rig.Profile.Canonical(sk.Names[i]);
            Quaternion Delta(int k) => name switch
            {
                "root" => Quat.FromAxisAngle(Vector3.UnitY, 0.3f * k),
                "spine" => Quat.FromAxisAngle(Vector3.UnitY, 0.2f * k),
                "upperarm-l" => Quat.FromAxisAngle(Vector3.UnitZ, 0.6f * k),
                "lowerarm-l" => Quat.FromAxisAngle(Vector3.UnitY, 0.5f * k + 0.3f),
                "upperarm-r" => Quat.FromAxisAngle(Vector3.UnitX, -0.4f * k),
                "lowerarm-r" => Quat.FromAxisAngle(Vector3.UnitY, -0.6f * k - 0.2f),
                "upperleg-l" => Quat.FromAxisAngle(Vector3.UnitX, -0.4f * k),
                "lowerleg-l" => Quat.FromAxisAngle(Vector3.UnitX, 0.5f * k + 0.1f),
                "upperleg-r" => Quat.FromAxisAngle(Vector3.UnitX, -0.2f * k),
                "lowerleg-r" => Quat.FromAxisAngle(Vector3.UnitX, 0.3f * k + 0.1f),
                _ => Quaternion.Identity,
            };
            var rot = new List<RfaRotKey>();
            Quaternion? prev = null;
            for (int k = 0; k < times.Length; k++)
            {
                var file = Quat.Conj(Quat.Mul(sk.RestLocal[i].Rotation, Delta(k)));
                if (prev is { } p) file = Quat.Align(file, p);
                prev = file;
                sbyte ease = name == "upperarm-l" ? (sbyte)20 : (sbyte)0;
                rot.Add(RfaRotKey.Quantize(times[k], file, ease, ease));
            }
            var rest = sk.RestLocal[i].Position;
            ImmutableArray<RfaPosKey> pos = name == "root"
                ? [RfaPosKey.Constant(160, rest), RfaPosKey.Constant(1120, rest + new Vector3(0.3f, 0.05f, 0.1f))]
                : [RfaPosKey.Constant(160, rest), RfaPosKey.Constant(1120, rest)];
            tracks[i] = new RfaBoneTrack(name.StartsWith("upperarm", StringComparison.Ordinal) ? 10f : 4f, [.. rot], pos);
        }
        return new RfaClip { StartTime = 160, EndTime = 1120, RampIn = 80, RampOut = 160, Bones = [.. tracks], Morph = morph ?? RfaMorph.Empty };
    }
}

public class RetargetTests
{
    private static RetargetRig Source => SyntheticRig.Rig("srcx-bdbn-");

    private static RetargetRig Target => SyntheticRig.Rig("tgtx-bdbn-", reversed: true, twistFrames: true, scale: 1.2f);

    // ── profiles ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuiltInProfilesCarryTheReferenceData()
    {
        Assert.Equal(["A", "female", "merc", "civilian"], RigProfiles.BuiltIn.Select(p => p.Name));
        Assert.All(RigProfiles.BuiltIn, p =>
        {
            Assert.Equal(4, p.IkChains.Length);
            Assert.Equal(0.10, p.IkPoleFade);
            Assert.Equal(320, p.IkMaxKeyGap);
            Assert.Equal(16, p.PrimaryChildren.Count);
            Assert.Equal("pelvis", p.PelvisBone);
            Assert.Empty(p.Validate());
        });
        Assert.Equal(25, RigProfiles.RigA.Bones.Length);
        Assert.Equal(27, RigProfiles.Merc.Bones.Length);
        Assert.Equal("root", RigProfiles.Merc.Bones.Single(b => b.Name == "spine03").Parent);
        Assert.Equal("head", RigProfiles.RigA.Canonical("ULT2-bdbn-Head"));
        Assert.Equal("spine04", RigProfiles.Civilian.Canonical("tech- 1spine01"));
        Assert.Equal("thumb-r2", RigProfiles.RigA.Canonical("tech- arm-r-thumb01"));
        Assert.Equal("xyz-bdbn-head", RigProfiles.RigA.Canonical("xyz-bdbn-head")); // only the stock prefixes
        Assert.Equal(new Vector3(0, -0.15f, 0), RigProfiles.Female.IkChains[0].PoleBias);
    }

    [Fact]
    public void FindForTellsTheStockRigsApartByTheirBoneLists()
    {
        foreach (var profile in RigProfiles.BuiltIn)
        {
            var (names, parents) = StockBoneList(profile, "abcd-bdbn-");
            Assert.Same(profile, RigProfiles.FindFor(names, parents));
        }
        // Rig A and the female rig share names; swapping two bones of rig A's list matches neither.
        var (a, ap) = StockBoneList(RigProfiles.RigA, "ult2-bdbn-");
        (a[0], a[1]) = (a[1], a[0]);
        Assert.Null(RigProfiles.FindFor(a, ap));
    }

    [Fact]
    public void ProfilesRoundTripThroughJson()
    {
        foreach (var profile in RigProfiles.BuiltIn.Append(SyntheticRig.Rig("q-bdbn-").Profile))
        {
            string json = profile.ToJson();
            var back = RigProfile.FromJson(json);
            Assert.Equal(json, back.ToJson());
            Assert.Equal(profile.Name, back.Name);
            Assert.Equal(profile.IkChains.ToArray(), back.IkChains.ToArray());
            Assert.Equal(profile.PrefixPatterns.ToArray(), back.PrefixPatterns.ToArray());
            Assert.Equal(profile.Renames.OrderBy(kv => kv.Key), back.Renames.OrderBy(kv => kv.Key));
            Assert.Equal(profile.PrimaryChildren.OrderBy(kv => kv.Key), back.PrimaryChildren.OrderBy(kv => kv.Key));
            Assert.Equal(profile.Bones.ToArray(), back.Bones.ToArray());
            Assert.Equal(profile.MeshNames.ToArray(), back.MeshNames.ToArray());
            Assert.Equal(profile.Canonical("tech- arm-l-upper"), back.Canonical("tech- arm-l-upper"));
        }
        Assert.Contains("\"poleBias\": [", RigProfiles.RigA.ToJson());
        var ex = Assert.Throws<FormatException>(() => RigProfile.FromJson("{ \"name\": 3 "));
        Assert.Contains("rig profile", ex.Message);
    }

    [Fact]
    public void GenericProfileFindsHumanoidLimbsAndNothingOnOtherSkeletons()
    {
        var rig = SyntheticRig.Rig("abcd-bdbn-", reversed: true);
        var p = rig.Profile;
        Assert.Equal(["upperarm-l", "upperarm-r", "upperleg-l", "upperleg-r"], p.IkChains.Select(c => c.Upper));
        Assert.Equal("pelvis", p.PelvisBone);
        Assert.Equal("root", p.RootBone);
        Assert.Equal("head", p.PrimaryChildren["spine"]);
        Assert.Equal("lowerarm-l", p.PrimaryChildren["upperarm-l"]);
        Assert.False(p.PrimaryChildren.ContainsKey("pelvis"));

        // Other spellings of the same limbs.
        var named = SyntheticRig.Rig("", rename: n => n switch
        {
            "upperarm-l" => "L_UpperArm", "lowerarm-l" => "L_Forearm", "hand-l" => "L_Hand",
            "upperleg-r" => "Thigh.R", "lowerleg-r" => "Calf.R", "foot-r" => "Foot.R",
            _ => n,
        });
        Assert.Equal(["l_upperarm", "upperarm-r", "upperleg-l", "thigh.r"], named.Profile.IkChains.Select(c => c.Upper));

        // A tail: no limbs, no pelvis.
        string[] names = ["root", "tail01", "tail02", "tail03", "fin-l", "fin-r"];
        int[] parents = [-1, 0, 1, 2, 0, 0];
        var tail = RigProfile.Generic(names, parents);
        Assert.Empty(tail.IkChains);
        Assert.Null(tail.PelvisBone);
        Assert.Equal("tail02", tail.PrimaryChildren["tail01"]);
    }

    // ── bone mapping ─────────────────────────────────────────────────────────

    [Fact]
    public void StockNameListsMapWithTheExpectedStatuses()
    {
        var (aNames, aParents) = StockBoneList(RigProfiles.RigA, "park-bdbn-");
        var (mNames, mParents) = StockBoneList(RigProfiles.Merc, "mrc1-bdbn-");
        var map = BoneMapper.Map(aNames, aParents, RigProfiles.RigA, mNames, mParents, RigProfiles.Merc);
        Assert.Equal(BoneMapStatus.Unmapped, map.Entries[15].Status);   // shoulderpad-l
        Assert.Equal(BoneMapStatus.Unmapped, map.Entries[16].Status);   // shoulderpad-r
        Assert.Equal(BoneMapStatus.Reparented, map.Entries[17].Status); // spine03 under root
        Assert.Equal(15, map.Entries[17].SourceIndex);
        Assert.Empty(map.UnusedSourceBones);

        // A generic target profile still lines up through the built-in tables.
        var (cNames, cParents) = StockBoneList(RigProfiles.Civilian, "");
        var generic = RigProfile.Generic(cNames, cParents) with { Renames = ImmutableDictionary<string, string>.Empty };
        var toCiv = BoneMapper.Map(aNames, aParents, RigProfiles.RigA, cNames, cParents, generic);
        Assert.Equal(25, toCiv.Entries.Count(e => e.IsMapped));
        Assert.All(toCiv.Entries.Where(e => e.IsMapped), e => Assert.Equal(BoneMatchKind.BuiltInTable, e.Match));
    }

    [Fact]
    public void FuzzyMatchingIsConservative()
    {
        var (aNames, aParents) = StockBoneList(RigProfiles.RigA, "ult2-bdbn-");
        string[] names = ["Root", "Pelvis", "Bip01 L Thigh", "L_Calf", "L_Foot", "Thigh_R", "Calf_R", "Foot_R", "Tail", "Hand", "LeftToes"];
        int[] parents = [-1, 0, 1, 2, 3, 1, 5, 6, 1, 1, 4];
        var target = RigProfile.Generic(names, parents);
        var map = BoneMapper.Map(aNames, aParents, RigProfiles.RigA, names, parents, target);
        string? Src(string t) => map.Entries[Array.IndexOf(names, t)].SourceName;

        Assert.Equal("ult2-bdbn-root", Src("Root"));
        Assert.Equal(BoneMatchKind.CanonicalName, map.Entries[1].Match);
        Assert.Equal("ult2-bdbn-upperleg-l", Src("Bip01 L Thigh"));
        Assert.Equal("ult2-bdbn-lowerleg-l", Src("L_Calf"));
        Assert.Equal("ult2-bdbn-foot-l", Src("L_Foot"));
        Assert.Equal("ult2-bdbn-upperleg-r", Src("Thigh_R"));
        Assert.Equal("ult2-bdbn-toes-l", Src("LeftToes"));
        Assert.Equal(BoneMatchKind.Fuzzy, map.Entries[2].Match);
        Assert.Null(Src("Tail"));  // nothing like it
        Assert.Null(Src("Hand"));  // no side: matches neither hand-l nor hand-r
        Assert.Equal(map.Entries.Count(e => e.IsMapped), map.Entries.Where(e => e.IsMapped).Select(e => e.SourceIndex).Distinct().Count());
    }

    [Fact]
    public void BoneMapEditsRecomputeStatusesAndRoundTripThroughJson()
    {
        var map = BoneMapper.Map(Source.Skeleton, Source.Profile, Target.Skeleton, Target.Profile);
        Assert.All(map.Entries, e => Assert.Equal(BoneMapStatus.Mapped, e.Status));
        Assert.Empty(map.Validate());

        int lower = Target.Profile.IndexOf(Target.Skeleton.Names, "lowerarm-l");
        int hand = Target.Profile.IndexOf(Target.Skeleton.Names, "hand-l");
        var edited = map.With(lower, -1);
        Assert.Equal(BoneMapStatus.Unmapped, edited.Entries[lower].Status);
        Assert.Equal(BoneMapStatus.ParentUnmapped, edited.Entries[hand].Status);
        Assert.Single(edited.UnusedSourceBones);
        var problem = Assert.Single(edited.Validate(), p => p.Severity == BoneMapSeverity.Error);
        Assert.Contains("tgtx-bdbn-lowerarm-l", problem.Message);
        Assert.Contains("unmap", problem.Message);

        var two = map.With("tgtx-bdbn-hand-r", "srcx-bdbn-hand-l");
        Assert.Contains(two.Validate(), p => p.Severity == BoneMapSeverity.Warning && p.Message.Contains("all follow"));
        Assert.Equal(BoneMatchKind.Manual, two.Entries[Target.Profile.IndexOf(Target.Skeleton.Names, "hand-r")].Match);

        string json = edited.ToJson();
        var back = BoneMap.FromJson(json);
        Assert.Equal(edited.Entries.ToArray(), back.Entries.ToArray());
        Assert.Equal(edited.UnusedSourceBones.ToArray(), back.UnusedSourceBones.ToArray());
        Assert.Equal(json, back.ToJson());
        Assert.Contains("\"status\": \"ParentUnmapped\"", json);
        Assert.Throws<FormatException>(() => BoneMap.FromJson("[]"));
    }

    // ── retarget ─────────────────────────────────────────────────────────────

    [Fact]
    public void SelfRetargetIsTheIdentity()
    {
        var rig = Source;
        var clip = SyntheticRig.Motion(rig);
        var result = Retargeter.Retarget(new RetargetRequest(clip, rig, rig) { Options = RetargetOptions.PureFrameOffset });
        Assert.True(result.Success, result.Error);
        Assert.True(ClipCompare.MaxRotationDegrees(clip, result.Clip!, engine: false) < 0.01);
        // Keys and eases transfer one for one.
        for (int i = 0; i < clip.BoneCount; i++)
        {
            Assert.Equal(clip.Bones[i].RotationKeys.Select(k => (k.Time, k.EaseIn, k.EaseOut)), result.Clip!.Bones[i].RotationKeys.Select(k => (k.Time, k.EaseIn, k.EaseOut)));
            Assert.Equal(clip.Bones[i].Weight, result.Clip.Bones[i].Weight);
        }
        Assert.Equal(clip.RampIn, result.Clip!.RampIn);
        Assert.Equal(clip.RampOut, result.Clip.RampOut);
    }

    [Fact]
    public void DifferentFramesOrderAndProportionsStillFollowTheSource()
    {
        var src = Source;
        var tgt = Target;
        var clip = SyntheticRig.Motion(src);

        // Rotation transfer only: every segment points where the source's does.
        var plain = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { Options = RetargetOptions.Default with { Ik = false } });
        Assert.True(plain.Success, plain.Error);
        var plainReport = RetargetReport.Build(clip, src.Skeleton, src.Profile, plain.Clip!, tgt.Skeleton, tgt.Profile, plain.BoneMap!, 80, ReportSampler.Reference);
        Assert.True(plainReport.StructureOk);
        Assert.True(plainReport.WorstSegmentDirection!.SegmentDirectionMaxDegrees < 0.05, plainReport.Summary);

        // With IK the hands and feet land on the source's in model space.
        var full = Retargeter.Retarget(new RetargetRequest(clip, src, tgt));
        Assert.True(full.Success, full.Error);
        // Sampled at the IK keys (160, 400, 640, 880, 1120: densified to 320 ticks); between them the
        // fast synthetic motion interpolates a centimetre or two away.
        var report = RetargetReport.Build(clip, src.Skeleton, src.Profile, full.Clip!, tgt.Skeleton, tgt.Profile, full.BoneMap!, 240);
        Assert.True(report.StructureOk, string.Join("; ", report.Checks.Where(c => !c.Passed).Select(c => c.Message)));
        Assert.True(report.HandsModelSpaceMaxCm < 0.5, report.Summary);
        Assert.True(report.FeetModelSpaceMaxCm < 0.5, report.Summary);
        Assert.Contains(full.ReportLines, l => l.StartsWith("root placed so the pelvis matches the source", StringComparison.Ordinal));
        Assert.Equal(4, full.ReportLines.Count(l => l.StartsWith("IK ", StringComparison.Ordinal)));
        Assert.Equal(5, report.JointOffsets.Length);
        // Built with the result's contacts, the report keeps what IK held apart from the head's offset.
        var withContacts = RetargetReport.Build(clip, src.Skeleton, src.Profile, full.Clip!, tgt.Skeleton, tgt.Profile, full.BoneMap!, 240, contacts: full.Contacts);
        Assert.Equal(4, withContacts.PinnedContacts.Length);
        Assert.Equal(4, withContacts.JointOffsets.Count(j => j.Pinned));
        Assert.False(withContacts.JointOffsets.Single(j => j.CanonicalName == "head").Pinned);
        Assert.All(full.Contacts, c => Assert.Equal("the source's position", c.HeldTo));
        Assert.Contains("pinned contacts", withContacts.Summary);
        // IK tracks are densified to at most 320 ticks apart, eases 0.
        var hand = full.Clip!.Bones[tgt.Profile.IndexOf(tgt.Skeleton.Names, "upperarm-l")];
        Assert.True(hand.RotationKeys.Zip(hand.RotationKeys.Skip(1)).All(p => p.Second.Time - p.First.Time <= 320));
        Assert.All(hand.RotationKeys, k => Assert.Equal(0, k.EaseIn));
        // Pelvis anchored: the target pelvis joint sits on the source pelvis joint.
        int sp = src.Profile.IndexOf(src.Skeleton.Names, "pelvis"), tp = tgt.Profile.IndexOf(tgt.Skeleton.Names, "pelvis");
        Assert.True(report.Joints.Single(j => j.TargetIndex == tp).ModelSpaceMaxCm < 0.1);
        Assert.Equal(sp, report.Joints.Single(j => j.TargetIndex == tp).SourceIndex);
    }

    [Fact]
    public void ExtraBonesHoldTheReferencePoseWithTheirAncestorsWeight()
    {
        var src = Source;
        var tgt = Target;
        var map = BoneMapper.Map(src.Skeleton, src.Profile, tgt.Skeleton, tgt.Profile);
        int hand = tgt.Profile.IndexOf(tgt.Skeleton.Names, "hand-l");
        var clip = SyntheticRig.Motion(src);
        var result = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { BoneMap = map.With(hand, -1), Options = RetargetOptions.Default with { Ik = false } });
        Assert.True(result.Success, result.Error);
        var track = result.Clip!.Bones[hand];
        Assert.Equal([160, 1120], track.RotationKeys.Select(k => k.Time));
        var want = ReferenceSampler.KeyRotation(tgt.ReferenceClip!.Bones[hand].RotationKeys[0]);
        Assert.All(track.RotationKeys, k => Assert.True(RefMath.AngleDegrees(want, ReferenceSampler.KeyRotation(k)) < 0.01));
        int lower = tgt.Profile.IndexOf(tgt.Skeleton.Names, "lowerarm-l");
        Assert.Equal(clip.Bones[map.SourceOf(lower)].Weight, track.Weight);
        Assert.Contains(result.ReportLines, l => l.StartsWith("hand-l", StringComparison.Ordinal) && l.Contains("static from tgtx-bdbn-stand.rfa"));
    }

    [Fact]
    public void UnsupportedCasesComeBackAsReadableErrors()
    {
        var src = Source;
        var tgt = Target;
        var clip = SyntheticRig.Motion(src);
        var map = BoneMapper.Map(src.Skeleton, src.Profile, tgt.Skeleton, tgt.Profile);

        // A mapped bone under an unmapped parent.
        int lower = tgt.Profile.IndexOf(tgt.Skeleton.Names, "lowerarm-l");
        var r1 = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { BoneMap = map.With(lower, -1) });
        Assert.False(r1.Success);
        Assert.Null(r1.Clip);
        Assert.Contains("has no source bone", r1.Error);
        Assert.Contains("Map 'tgtx-bdbn-lowerarm-l'", r1.Error);
        Assert.DoesNotContain("Exception", r1.Error);

        // The target root on a non-root source bone.
        int root = tgt.Profile.IndexOf(tgt.Skeleton.Names, "root");
        var r2 = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { BoneMap = map.With(root, src.Profile.IndexOf(src.Skeleton.Names, "pelvis")) });
        Assert.False(r2.Success);
        Assert.Contains("source's root", r2.Error);

        // A clip for another skeleton.
        var shorter = clip with { Bones = clip.Bones.RemoveAt(0) };
        var r3 = Retargeter.Retarget(new RetargetRequest(shorter, src, tgt));
        Assert.False(r3.Success);
        Assert.Contains($"{SyntheticRig.BoneCount - 1} bones but the source mesh has {SyntheticRig.BoneCount}", r3.Error);

        // No reference clip.
        var r4 = Retargeter.Retarget(new RetargetRequest(clip, src, tgt with { ReferenceClip = null }));
        Assert.False(r4.Success);
        Assert.Contains("stand clip", r4.Error);
        var r4b = Retargeter.Retarget(new RetargetRequest(clip, src, tgt with { ReferenceClip = null })
        {
            Options = RetargetOptions.Default with { BoneLengths = BoneLengthSource.TargetBind, ExtraBonePose = ExtraBonePose.Bind },
        });
        Assert.True(r4b.Success, r4b.Error);

        // Anchor pelvis without a pelvis.
        var noPelvis = tgt with { Profile = tgt.Profile with { PelvisBone = null } };
        var r5 = Retargeter.Retarget(new RetargetRequest(clip, src, noPelvis));
        Assert.False(r5.Success);
        Assert.Contains("choose another root mode", r5.Error);

        // A bone map for other skeletons.
        var r6 = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { BoneMap = BoneMapper.Map(src.Skeleton, src.Profile, src.Skeleton, src.Profile) });
        Assert.False(r6.Success);
        Assert.Contains("other skeletons", r6.Error);

        // A profile with a broken prefix pattern.
        var r7 = Retargeter.Retarget(new RetargetRequest(clip, src, tgt with { Profile = tgt.Profile with { PrefixPatterns = ["(unclosed"] } }));
        Assert.False(r7.Success);
        Assert.Contains("not a valid regular expression", r7.Error);
        Assert.NotEmpty((tgt.Profile with { PrefixPatterns = ["(unclosed"] }).Validate());

        Assert.Throws<ArgumentNullException>(() => Retargeter.Retarget(null!));
    }

    [Fact]
    public void MorphDataIsDroppedWithAWarning()
    {
        var src = Source;
        var morph = new RfaMorph([0, 1], 1, [160], new RfaMorphBounds(Vector3.Zero, Vector3.One), [1, 2, 3, 4, 5, 6], []);
        var clip = SyntheticRig.Motion(src, morph);
        var result = Retargeter.Retarget(new RetargetRequest(clip, src, Target));
        Assert.True(result.Success, result.Error);
        Assert.True(result.Clip!.Morph.IsEmpty);
        Assert.Contains(result.Warnings, w => w.Contains("morph") && w.Contains("dropped"));
    }

    [Fact]
    public void RootModesBoneLengthsAndResampling()
    {
        var src = Source;
        var tgt = Target;
        var clip = SyntheticRig.Motion(src);
        int sRoot = src.Profile.IndexOf(src.Skeleton.Names, "root"), tRoot = tgt.Profile.IndexOf(tgt.Skeleton.Names, "root");

        var copy = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { Options = RetargetOptions.Default with { RootMode = RootMode.CopySource } });
        Assert.Equal(clip.Bones[sRoot].PositionKeys.ToArray(), copy.Clip!.Bones[tRoot].PositionKeys.ToArray());

        var keep = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { Options = RetargetOptions.Default with { RootMode = RootMode.KeepInPlace } });
        Assert.All(keep.Clip!.Bones[tRoot].PositionKeys, k => Assert.Equal(tgt.ReferenceClip!.Bones[tRoot].PositionKeys[0].Position, k.Position));

        // Hip height: horizontal travel kept as the source's; with scaled strides, scaled by the leg ratio.
        var hips = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { Options = RetargetPresets.Options(RetargetPreset.Locomotion) });
        Assert.True(hips.Success, hips.Error);
        var travel = hips.Clip!.Bones[tRoot].PositionKeys[^1].Position - hips.Clip.Bones[tRoot].PositionKeys[0].Position;
        Assert.Equal(0.3f, travel.X, 3);
        var strides = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { Options = RetargetPresets.Options(RetargetPreset.Locomotion) with { ScaleStride = true } });
        Assert.True(strides.Success, strides.Error);
        travel = strides.Clip!.Bones[tRoot].PositionKeys[^1].Position - strides.Clip.Bones[tRoot].PositionKeys[0].Position;
        Assert.Equal(0.3f * (float)strides.Ground!.LegRatio, travel.X, 3);

        var bind = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { Options = RetargetOptions.Default with { BoneLengths = BoneLengthSource.TargetBind } });
        int lower = tgt.Profile.IndexOf(tgt.Skeleton.Names, "lowerarm-l");
        Assert.True(Vector3.Distance(tgt.Skeleton.RestLocal[lower].Position, bind.Clip!.Bones[lower].PositionKeys[0].Position) < 1e-6f);

        var own = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { Options = RetargetOptions.PureFrameOffset with { BoneLengths = BoneLengthSource.Source } });
        int sLower = src.Profile.IndexOf(src.Skeleton.Names, "lowerarm-l");
        Assert.Equal(clip.Bones[sLower].PositionKeys.ToArray(), own.Clip!.Bones[lower].PositionKeys.ToArray());

        var grid = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { Options = RetargetOptions.PureFrameOffset with { ResampleStep = 160 } });
        Assert.All(grid.Clip!.Bones, b => Assert.Equal(Enumerable.Range(0, 7).Select(k => 160 + 160 * k), b.RotationKeys.Select(k => k.Time)));
        Assert.All(grid.Clip.Bones.SelectMany(b => b.RotationKeys), k => Assert.Equal(0, k.EaseIn));

        var someIk = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { Options = RetargetOptions.Default with { DisabledIkChains = ["upperleg-l", "UPPERLEG-R"] } });
        Assert.Equal(2, someIk.ReportLines.Count(l => l.StartsWith("IK ", StringComparison.Ordinal) && l.Contains("keys")));
        Assert.Equal(2, someIk.ReportLines.Count(l => l.EndsWith(" off", StringComparison.Ordinal)));
    }

    [Fact]
    public void CrossBranchBonesAreEvaluatedInModelSpace()
    {
        // Drive the target's left hand from the source's left foot: the hand's parent (lowerarm-l)
        // follows the source lowerarm-l, which is not an ancestor of the foot.
        var src = Source;
        var tgt = Target;
        var clip = SyntheticRig.Motion(src);
        var map = BoneMapper.Map(src.Skeleton, src.Profile, tgt.Skeleton, tgt.Profile);
        int tUpperLegL = tgt.Profile.IndexOf(tgt.Skeleton.Names, "hand-l");
        int sUpperArmL = src.Profile.IndexOf(src.Skeleton.Names, "foot-l");
        var cross = map.With(tUpperLegL, sUpperArmL);
        Assert.Equal(BoneMapStatus.CrossBranch, cross.Entries[tUpperLegL].Status);
        var result = Retargeter.Retarget(new RetargetRequest(clip, src, tgt) { BoneMap = cross, Options = RetargetOptions.PureFrameOffset });
        Assert.True(result.Success, result.Error);
        Assert.Contains(result.ReportLines, l => l.StartsWith("hand-l", StringComparison.Ordinal) && l.Contains("cross-branch"));

        // Its model-space rotation follows the source arm's times the frame offset.
        var pose = new Pose(tgt.Skeleton);
        var spose = new Pose(src.Skeleton);
        var c0 = Quat.Mul(Quat.Conj(src.Skeleton.RestWorld[sUpperArmL].Rotation), tgt.Skeleton.RestWorld[tUpperLegL].Rotation);
        // At the resampled key times (the union of both source chains' keys); between keys the
        // slerp of the composed rotation is not the composition of the slerps.
        foreach (int t in new[] { 160, 640, 1120 })
        {
            pose.Sample(result.Clip!, t);
            spose.Sample(clip, t);
            var want = Quat.Mul(spose.World[sUpperArmL].Rotation, c0);
            Assert.True(Quat.AngleDegrees(want, pose.World[tUpperLegL].Rotation) < 0.2f, $"t={t}");
        }
    }

    // ── IK helper ────────────────────────────────────────────────────────────

    [Fact]
    public void TwoBoneIkReachesTheTargetAndBendsTowardThePole()
    {
        var s = new Vector3(0, 1, 0);
        var t = new Vector3(0.5f, 1, 0);
        var sol = TwoBoneIk.Solve(s, new Vector3(0.25f, 0.8f, 0), t, 0.3f, 0.3f, Vector3.Zero);
        Assert.False(sol.Clamped);
        Assert.True(Vector3.Distance(t, sol.End) < 1e-5f);
        Assert.Equal(0.3f, Vector3.Distance(s, sol.Elbow), 4);
        Assert.Equal(0.3f, Vector3.Distance(sol.Elbow, sol.End), 4);
        Assert.True(sol.Elbow.Y < 1f); // bent toward the source elbow (down)

        // Straight source limb: the pole bias decides.
        var straight = TwoBoneIk.Solve(s, new Vector3(0.25f, 1, 0), t, 0.3f, 0.3f, new Vector3(0, 0, 0.15f));
        Assert.True(straight.Elbow.Z > 0.1f);

        // Out of reach: clamped onto the line.
        var far = TwoBoneIk.Solve(s, new Vector3(0.5f, 0.9f, 0), new Vector3(2, 1, 0), 0.3f, 0.3f, Vector3.Zero);
        Assert.True(far.Clamped);
        Assert.Equal(0.6f, Vector3.Distance(s, far.End), 3);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>A built-in profile's bone list as stored names (prefixed; civilian names spelt as tech01 spells them) and parents.</summary>
    internal static (string[] Names, int[] Parents) StockBoneList(RigProfile profile, string prefix)
    {
        var bones = profile.Bones;
        var reverse = RigProfiles.CivilianRenames.ToDictionary(kv => kv.Value, kv => kv.Key);
        string Stored(string canonical) => profile == RigProfiles.Civilian ? reverse[canonical] : prefix + canonical;
        var names = bones.Select(b => Stored(b.Name)).ToArray();
        var parents = bones.Select(b => b.Parent is null ? -1 : bones.ToList().FindIndex(x => x.Name == b.Parent)).ToArray();
        return (names, parents);
    }
}
