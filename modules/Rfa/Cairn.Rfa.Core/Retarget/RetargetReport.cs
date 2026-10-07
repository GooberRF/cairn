using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Retarget;

/// <summary>Which sampler a report evaluates clips with.</summary>
public enum ReportSampler
{
    /// <summary><see cref="ClipSampler"/>: what the game shows (int16 slerp store, single precision). The default.</summary>
    Engine,

    /// <summary>The reference's float sampler (<c>rfanim.py</c>), what <c>verify.py</c> measured with.</summary>
    Reference,
}

/// <summary>One named structural check of an output clip.</summary>
/// <param name="Name">What is checked.</param>
/// <param name="Passed">True when the clip passes.</param>
/// <param name="Message">The finding in plain words (what is wrong, when it fails).</param>
public sealed record ReportCheck(string Name, bool Passed, string Message);

/// <summary>One mapped target joint compared with its source joint over the sampled times.</summary>
/// <param name="TargetIndex">Target bone index.</param>
/// <param name="BoneName">Target bone name as stored.</param>
/// <param name="CanonicalName">Target canonical name.</param>
/// <param name="SourceIndex">The source bone it follows.</param>
/// <param name="PelvisRelativeMaxCm">Largest difference of the joint position relative to the pelvis, cm.</param>
/// <param name="PelvisRelativeMeanCm">Mean of that difference, cm.</param>
/// <param name="ModelSpaceMaxCm">Largest difference of the joint position in model space, cm.</param>
/// <param name="SegmentDirectionMaxDegrees">Largest angle between the joint -> primary child directions, or null when the joint has no comparable primary child.</param>
/// <param name="PrimaryChild">Canonical name of the primary child measured, or null.</param>
public sealed record JointError(
    int TargetIndex, string BoneName, string CanonicalName, int SourceIndex,
    double PelvisRelativeMaxCm, double PelvisRelativeMeanCm, double ModelSpaceMaxCm,
    double? SegmentDirectionMaxDegrees, string? PrimaryChild);

/// <summary>
/// A hand, foot or head compared with the source's in model space. Informational: unless IK pins the
/// joint (see <see cref="RetargetReport.PinnedContacts"/>), the offset is the two rigs' proportions,
/// not an error.
/// </summary>
/// <param name="CanonicalName">Target canonical name.</param>
/// <param name="TargetIndex">Target bone index.</param>
/// <param name="ModelSpaceMaxCm">Largest model-space distance from the source joint over the samples, cm.</param>
/// <param name="SourceAtStart">The source joint at the first sample (model space, metres).</param>
/// <param name="TargetAtStart">The target joint at the first sample.</param>
/// <param name="Pinned">True when an IK chain held this joint (its error is then in <see cref="RetargetReport.PinnedContacts"/>).</param>
public sealed record JointOffset(string CanonicalName, int TargetIndex, double ModelSpaceMaxCm, Vector3 SourceAtStart, Vector3 TargetAtStart, bool Pinned);

/// <summary>
/// The checks of the reference's <c>verify.py</c> for one retargeted clip: the output checklist of
/// retarget_method.md section 10 (structure), and FK against the source sampled every
/// <see cref="SampleStep"/> ticks — per joint the pelvis-relative and model-space position error and
/// the direction error of the segment to its primary child, the hands/feet/head in model space, and
/// the worst joint and segment. Sampled with the engine's <see cref="ClipSampler"/> by default.
/// Limb IK moves elbows and knees on purpose (hands and feet are pinned), so upper/lower limb
/// segment directions differ by the proportions; with IK off every aligned segment is ~0 degrees.
/// <para>Two kinds of distance are kept apart: <see cref="PinnedContacts"/> (what IK was asked to hold;
/// ~0 unless a limb could not reach, which is then said) and <see cref="JointOffsets"/> (where hands,
/// feet and head ended up against the source's: proportions, not errors, unless pinned).</para>
/// </summary>
public sealed record RetargetReport
{
    /// <summary>Structural checks (section 10), each pass/fail with a message.</summary>
    public ImmutableArray<ReportCheck> Checks { get; init; } = [];

    /// <summary>True when every structural check passed.</summary>
    public bool StructureOk => Checks.All(c => c.Passed);

    /// <summary>Per mapped joint, in target index order.</summary>
    public ImmutableArray<JointError> Joints { get; init; } = [];

    /// <summary>
    /// What IK was asked to hold and how well the clip holds it (from <see cref="RetargetResult.Contacts"/>;
    /// empty when no IK ran or the report was built without the result). ~0 cm unless a limb could not reach.
    /// </summary>
    public ImmutableArray<PinnedContact> PinnedContacts { get; init; } = [];

    /// <summary>The pinned contact with the largest error, or null.</summary>
    public PinnedContact? WorstPinnedContact => PinnedContacts.IsDefaultOrEmpty ? null : PinnedContacts.MaxBy(c => c.MaxErrorCm);

    /// <summary>Pinned contacts whose limb was fully stretched (out of reach) at some time.</summary>
    public int StretchedContacts => PinnedContacts.IsDefaultOrEmpty ? 0 : PinnedContacts.Count(c => c.Stretched);

    /// <summary>
    /// Hands, feet and head (IK chain ends plus the head) against the source's, in model space.
    /// Informational: a joint IK did not pin moves with the target's proportions.
    /// </summary>
    public ImmutableArray<JointOffset> JointOffsets { get; init; } = [];

    /// <summary>Largest model-space hand offset from the source's, cm (NaN when the rig has no hands).</summary>
    public double HandsModelSpaceMaxCm { get; init; } = double.NaN;

    /// <summary>Largest model-space foot offset from the source's, cm (NaN when the rig has no feet).</summary>
    public double FeetModelSpaceMaxCm { get; init; } = double.NaN;

    /// <summary>The body joint (fingers, thumbs and toes left out) with the largest pelvis-relative error.</summary>
    public JointError? WorstPelvisRelative { get; init; }

    /// <summary>The joint with the largest segment direction error.</summary>
    public JointError? WorstSegmentDirection { get; init; }

    /// <summary>Ticks between samples.</summary>
    public int SampleStep { get; init; }

    /// <summary>Number of sampled times.</summary>
    public int SampleCount { get; init; }

    /// <summary>The sampler used.</summary>
    public ReportSampler Sampler { get; init; }

    /// <summary>
    /// The headline: structure, what IK held (pinned contacts) and the worst segment direction. Offsets that only
    /// reflect the two rigs' proportions are in <see cref="ProportionSummary"/>, not here (phase 7b: "hands 27 cm
    /// from the source's" read as an error when it is the target's shorter arms).
    /// </summary>
    public string Summary => string.Format(CultureInfo.InvariantCulture,
        "structure {0}; pinned contacts {1}; worst segment direction {2}",
        StructureOk ? "ok" : "FAIL",
        PinnedSummary,
        WorstSegmentDirection is { SegmentDirectionMaxDegrees: { } d } w ? $"{d:0.0} deg ({w.CanonicalName})" : "-");

    /// <summary>
    /// Offsets from the source's that follow from the rigs' different proportions (not errors): the worst
    /// pelvis-relative joint and the largest hand and foot offsets in model space.
    /// </summary>
    public string ProportionSummary => string.Format(CultureInfo.InvariantCulture,
        "proportions (not errors): worst pelvis-relative joint {0}; hands {1:0.0} cm, feet {2:0.0} cm from the source's (model space)",
        WorstPelvisRelative is { } p ? $"{p.PelvisRelativeMaxCm:0.0} cm ({p.CanonicalName})" : "-",
        HandsModelSpaceMaxCm, FeetModelSpaceMaxCm);

    /// <summary>"0.0 cm (foot-l)", "4.1 cm (foot-r, leg fully stretched)", or "none" when nothing was pinned.</summary>
    public string PinnedSummary => WorstPinnedContact is { } c
        ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} cm ({1}{2})", c.MaxErrorCm, c.EndBone,
            StretchedContacts > 0 ? $", {StretchedContacts} limb{(StretchedContacts == 1 ? "" : "s")} fully stretched" : string.Empty)
        : "none";

    /// <summary>Builds the report for one output clip.</summary>
    /// <param name="source">The source clip.</param>
    /// <param name="sourceSkeleton">The source skeleton (its rest mesh).</param>
    /// <param name="sourceProfile">The source profile (pelvis).</param>
    /// <param name="output">The retargeted clip.</param>
    /// <param name="targetSkeleton">The target skeleton.</param>
    /// <param name="targetProfile">The target profile (canonical names, primary children, IK chain ends, pelvis).</param>
    /// <param name="map">The bone map used.</param>
    /// <param name="sampleStep">Ticks between samples (160 = every frame at 30 fps).</param>
    /// <param name="sampler">Engine (default) or the reference's float sampler.</param>
    /// <param name="contacts">The retarget's <see cref="RetargetResult.Contacts"/> (what IK held), or null.</param>
    public static RetargetReport Build(
        RfaClip source, Skeleton sourceSkeleton, RigProfile sourceProfile,
        RfaClip output, Skeleton targetSkeleton, RigProfile targetProfile,
        BoneMap map, int sampleStep = 160, ReportSampler sampler = ReportSampler.Engine,
        IReadOnlyList<PinnedContact>? contacts = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceSkeleton);
        ArgumentNullException.ThrowIfNull(sourceProfile);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(targetSkeleton);
        ArgumentNullException.ThrowIfNull(targetProfile);
        ArgumentNullException.ThrowIfNull(map);
        if (sampleStep <= 0) throw new ArgumentOutOfRangeException(nameof(sampleStep), "The sample step must be above 0 ticks.");

        var tNames = targetProfile.CanonicalNames(targetSkeleton.Names);
        var checks = StructureChecks(output, targetSkeleton, tNames);
        var report = new RetargetReport { Checks = checks, SampleStep = sampleStep, Sampler = sampler, PinnedContacts = contacts is null ? [] : [.. contacts] };
        if (output.BoneCount != targetSkeleton.Count || source.BoneCount != sourceSkeleton.Count || map.Entries.Length != targetSkeleton.Count)
            return report;

        var times = SampleTimes(source, sampleStep);
        int sp = Pelvis(sourceProfile, sourceSkeleton);
        int tp = Pelvis(targetProfile, targetSkeleton);
        int nt = targetSkeleton.Count;
        var smap = map.ToArray();
        for (int i = 0; i < nt; i++)
        {
            if (smap[i] >= sourceSkeleton.Count) smap[i] = -1;
        }

        var childOf = new int[nt];
        for (int i = 0; i < nt; i++)
        {
            childOf[i] = targetProfile.PrimaryChildren.TryGetValue(tNames[i], out var child) ? tNames.IndexOf(child) : -1;
        }

        var perrMax = new double[nt];
        var perrSum = new double[nt];
        var dirMax = new double?[nt];
        var modelMax = new double[nt];
        Vector3[]? srcStart = null, tgtStart = null;
        var srcLocal = new Rigid[sourceSkeleton.Count];
        var srcWorld = new Rigid[sourceSkeleton.Count];
        var tgtLocal = new Rigid[nt];
        var tgtWorld = new Rigid[nt];
        foreach (int t in times)
        {
            Sample(source, sourceSkeleton, t, sampler, srcLocal, srcWorld);
            Sample(output, targetSkeleton, t, sampler, tgtLocal, tgtWorld);
            if (srcStart is null)
            {
                srcStart = [.. srcWorld.Select(w => w.Position)];
                tgtStart = [.. tgtWorld.Select(w => w.Position)];
            }
            for (int i = 0; i < nt; i++)
            {
                int si = smap[i];
                if (si < 0) continue;
                double perr = Dist(srcWorld[si].Position - srcWorld[sp].Position, tgtWorld[i].Position - tgtWorld[tp].Position);
                perrMax[i] = Math.Max(perrMax[i], perr);
                perrSum[i] += perr / times.Count;
                modelMax[i] = Math.Max(modelMax[i], Dist(srcWorld[si].Position, tgtWorld[i].Position));
                int c = childOf[i];
                if (c >= 0 && smap[c] >= 0)
                {
                    var ds = Direction(srcWorld[si].Position, srcWorld[smap[c]].Position);
                    var dt = Direction(tgtWorld[i].Position, tgtWorld[c].Position);
                    if (ds is { } a && dt is { } b)
                    {
                        double deg = Math.Acos(Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1.0, 1.0)) * (180.0 / Math.PI);
                        dirMax[i] = Math.Max(dirMax[i] ?? 0.0, deg);
                    }
                }
            }
        }

        var joints = new List<JointError>();
        for (int i = 0; i < nt; i++)
        {
            if (smap[i] < 0) continue;
            joints.Add(new JointError(i, targetSkeleton.Names[i], tNames[i], smap[i], perrMax[i] * 100, perrSum[i] * 100, modelMax[i] * 100,
                dirMax[i], childOf[i] >= 0 && smap[childOf[i]] >= 0 ? tNames[childOf[i]] : null));
        }

        var ends = new List<JointOffset>();
        var endBones = targetProfile.IkChains.Select(c => c.End).Append("head").Distinct();
        var pinned = new HashSet<int>(report.PinnedContacts.Select(c => c.TargetIndex));
        foreach (string name in endBones)
        {
            int i = tNames.IndexOf(name);
            if (i < 0 || smap[i] < 0) continue;
            ends.Add(new JointOffset(name, i, modelMax[i] * 100, srcStart![smap[i]], tgtStart![i], pinned.Contains(i)));
        }

        double Worst(string word) =>
            ends.Where(e => BoneTokens.Parse(e.CanonicalName).Tokens.Contains(word)).Select(e => e.ModelSpaceMaxCm).DefaultIfEmpty(double.NaN).Max();

        var body = joints.Where(j => !BoneTokens.Parse(j.CanonicalName).Tokens.Any(t => t is "finger" or "thumb" or "toe")).ToList();
        return report with
        {
            Joints = [.. joints],
            JointOffsets = [.. ends],
            HandsModelSpaceMaxCm = Worst("hand"),
            FeetModelSpaceMaxCm = Worst("foot"),
            WorstPelvisRelative = body.OrderByDescending(j => j.PelvisRelativeMaxCm).FirstOrDefault(),
            WorstSegmentDirection = joints.Where(j => j.SegmentDirectionMaxDegrees is not null).OrderByDescending(j => j.SegmentDirectionMaxDegrees).FirstOrDefault(),
            SampleCount = times.Count,
        };
    }

    /// <summary>Start, start + step, ... while before the end, then the end (verify.py's <c>sample_times</c>).</summary>
    internal static List<int> SampleTimes(RfaClip clip, int step)
    {
        var times = new List<int>();
        for (long t = clip.StartTime; t < clip.EndTime; t += step) times.Add((int)t);
        times.Add(clip.EndTime);
        return times;
    }

    /// <summary>The section 10 checklist plus verify.py's round trip, for any clip against a skeleton.</summary>
    public static ImmutableArray<ReportCheck> StructureChecks(RfaClip clip, Skeleton skeleton, IReadOnlyList<string>? boneNames = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(skeleton);
        string Name(int i) => boneNames is not null && i < boneNames.Count ? boneNames[i] : i < skeleton.Count ? skeleton.Names[i] : $"bone {i}";
        var checks = new List<ReportCheck>();

        // Round trip.
        try
        {
            byte[] bytes = RfaWriter.Write(clip);
            byte[] again = RfaWriter.Write(RfaReader.Read(bytes, "output.rfa"));
            checks.Add(bytes.AsSpan().SequenceEqual(again)
                ? new ReportCheck("Round trip", true, $"Writes, reads back and re-writes identically ({bytes.Length} bytes).")
                : new ReportCheck("Round trip", false, "The clip does not come back identical after writing and reading it."));
        }
        catch (Exception ex) when (ex is ArgumentException or Cairn.Formats.AssetFormatException)
        {
            checks.Add(new ReportCheck("Round trip", false, $"The clip cannot be written and read back: {ex.Message}"));
        }

        checks.Add(clip.BoneCount == skeleton.Count
            ? new ReportCheck("Bone count", true, $"{clip.BoneCount} bones, as the target mesh.")
            : new ReportCheck("Bone count", false, $"The clip has {clip.BoneCount} bones but the target mesh has {skeleton.Count}; the game matches bones by index and would play garbage."));

        var keyProblems = new List<string>();
        var timeProblems = new List<string>();
        var unitProblems = new List<string>();
        var signProblems = new List<string>();
        for (int i = 0; i < clip.BoneCount; i++)
        {
            var b = clip.Bones[i];
            if (b.RotationKeys.Length < 1) keyProblems.Add($"{Name(i)} has no rotation key");
            if (b.PositionKeys.Length < 2) keyProblems.Add($"{Name(i)} has {b.PositionKeys.Length} position key(s)");
            if (!TimesOk(b.RotationKeys.Select(k => k.Time), clip, out string? why) || !TimesOk(b.PositionKeys.Select(k => k.Time), clip, out why))
                timeProblems.Add($"{Name(i)}: {why}");
            RfaRotKey? prev = null;
            foreach (var k in b.RotationKeys)
            {
                double x = k.X / RefMath.QuatScale, y = k.Y / RefMath.QuatScale, z = k.Z / RefMath.QuatScale, w = k.W / RefMath.QuatScale;
                double n = Math.Sqrt(x * x + y * y + z * z + w * w);
                if (Math.Abs(n - 1.0) > 0.002) unitProblems.Add(string.Format(CultureInfo.InvariantCulture, "{0} at tick {1} has length {2:0.0000}", Name(i), k.Time, n));
                if (prev is { } p && (long)p.X * k.X + (long)p.Y * k.Y + (long)p.Z * k.Z + (long)p.W * k.W < 0)
                    signProblems.Add($"{Name(i)} flips sign at tick {k.Time}");
                prev = k;
            }
        }
        checks.Add(Check("Keys present", keyProblems, "Every bone has at least one rotation key and at least two position keys."));
        checks.Add(Check("Key times", timeProblems, "Key times are strictly increasing and inside [start, end]."));
        checks.Add(Check("Unit quaternions", unitProblems, "Every rotation key is unit length within 0.002 after dividing by 16383."));
        checks.Add(Check("Sign continuity", signProblems, "Consecutive rotation keys stay in the same hemisphere."));
        return [.. checks];

        static ReportCheck Check(string name, List<string> problems, string ok) =>
            problems.Count == 0
                ? new ReportCheck(name, true, ok)
                : new ReportCheck(name, false, string.Join("; ", problems.Take(5)) + (problems.Count > 5 ? $"; and {problems.Count - 5} more" : string.Empty) + ".");
    }

    private static bool TimesOk(IEnumerable<int> times, RfaClip clip, out string? why)
    {
        int? prev = null;
        foreach (int t in times)
        {
            if (t < clip.StartTime || t > clip.EndTime)
            {
                why = $"a key at tick {t} is outside [{clip.StartTime}, {clip.EndTime}]";
                return false;
            }
            if (prev is { } p && t <= p)
            {
                why = $"key times not strictly increasing at tick {t}";
                return false;
            }
            prev = t;
        }
        why = null;
        return true;
    }

    private static int Pelvis(RigProfile profile, Skeleton skeleton)
    {
        int i = profile.IndexOf(skeleton.Names, profile.PelvisBone);
        if (i >= 0) return i;
        i = profile.IndexOf(skeleton.Names, profile.RootBone);
        return i >= 0 ? i : Math.Max(0, skeleton.EffectiveParents.IndexOf(-1));
    }

    private static void Sample(RfaClip clip, Skeleton skeleton, int t, ReportSampler sampler, Rigid[] local, Rigid[] world)
    {
        if (sampler == ReportSampler.Engine)
        {
            ClipSampler.SampleLocals(clip, t, local);
            ForwardKinematics.Solve(skeleton, local, world);
            return;
        }
        var dl = new DRigid[skeleton.Count];
        for (int i = 0; i < dl.Length; i++)
        {
            var b = clip.Bones[i];
            dl[i] = new DRigid(ReferenceSampler.SampleRotation(b.RotationKeys.AsSpan(), t), ReferenceSampler.SamplePosition(ReferenceSampler.Positions(b.PositionKeys.AsSpan()), t));
        }
        var dw = ReferenceSampler.Solve(skeleton.EffectiveParents.AsSpan(), skeleton.EvaluationOrder.AsSpan(), dl);
        for (int i = 0; i < dw.Length; i++) world[i] = new Rigid(dw[i].Rot.ToQuaternion(), dw[i].Pos.ToVector3());
    }

    private static double Dist(Vector3 a, Vector3 b)
    {
        double dx = (double)a.X - b.X, dy = (double)a.Y - b.Y, dz = (double)a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static (double X, double Y, double Z)? Direction(Vector3 from, Vector3 to)
    {
        double x = (double)to.X - from.X, y = (double)to.Y - from.Y, z = (double)to.Z - from.Z;
        double n = Math.Sqrt(x * x + y * y + z * z);
        return n > 1e-5 ? (x / n, y / n, z / n) : null;
    }
}
