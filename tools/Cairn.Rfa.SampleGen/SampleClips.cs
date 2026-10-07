using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.SampleGen;

/// <summary>
/// The sample clips for <see cref="SampleFigure"/>: a looping idle, a looping walk in place, a wave
/// played as an action (eased keys, weight on the right arm only), and a clip broken on purpose.
/// Rotations go through <see cref="ClipEdit.QuantizeRotation"/> (file convention, sign continuity,
/// never longer than 1); positions are the joints' offsets from their parents, rounded to 0.1 mm so
/// the bytes do not depend on the last bit of a sine.
/// </summary>
public static class SampleClips
{
    /// <summary>Every clip starts at frame 1 (160 ticks), as stock clips do.</summary>
    public const int Start = RfaClip.TicksPerFrame;

    /// <summary>The time of frame <paramref name="frame"/> counted from the clip's start.</summary>
    public static int Frame(int frame) => Start + frame * RfaClip.TicksPerFrame;

    /// <summary>The rig's bind pose relative to each parent (what every position key holds at rest).</summary>
    private static readonly ImmutableArray<Rigid> Rest = Skeleton.FromBones(SampleFigure.StoredBones).RestLocal;

    private static int BoneCount => SampleFigure.Bones.Length;

    /// <summary>One bone's pose at a key: Euler angles in degrees (pitch X, yaw Y, roll Z) and an offset from the rest position.</summary>
    private readonly record struct BonePose(Vector3 Euler, Vector3 Offset);

    // ── Idle ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A two-second breathing idle (64 frames, a key every 8): the weight sways from foot to foot, the
    /// chest rises and falls, the head looks slowly left and right. The last key is the first one.
    /// </summary>
    public static RfaClip Idle() => Loop(frames: 64, keys: 8, weight: 1f, (bone, phase) =>
    {
        float s = Sin(phase), s2 = Sin(2 * phase), c2 = Cos(2 * phase);
        return bone switch
        {
            SampleFigure.Pelvis => new BonePose(new(0, 0, 1.5f * s), new(0.01f * s, 0.004f * (c2 - 1), 0)),
            SampleFigure.Spine => Turn(1.5f * s2, 0, -1.5f * s),
            SampleFigure.Head => Turn(-1.5f * s2, 8 * s, 0),
            SampleFigure.UpperArmL => Turn(2 * s, 0, -(4 + s2)),
            SampleFigure.UpperArmR => Turn(-2 * s, 0, 4 + s2),
            SampleFigure.ForearmL or SampleFigure.ForearmR => Turn(-(10 + 2 * s2), 0, 0),
            SampleFigure.HandL or SampleFigure.HandR => Turn(-5, 0, 0),
            SampleFigure.ThighL or SampleFigure.ThighR => Turn(0, 0, -1.5f * s),
            _ => default,
        };
    });

    // ── Walk ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A walk cycle in place (32 frames, a key every 4): legs swing 25 degrees each way with the knee
    /// folding through the swing, arms swing against the legs, the pelvis twists and bobs twice per
    /// cycle. The last key is the first one.
    /// </summary>
    public static RfaClip Walk() => Loop(frames: 32, keys: 8, weight: 1f, (bone, phase) =>
    {
        float s = Sin(phase), c = Cos(phase), c2 = Cos(2 * phase);
        float thighL = -25 * s, thighR = 25 * s;       // negative pitch swings a leg forward (+Z)
        float kneeL = 20 + 18 * c, kneeR = 20 - 18 * c; // positive pitch folds the shin back
        return bone switch
        {
            SampleFigure.Pelvis => new BonePose(new(0, 6 * s, 2 * s), new(0, 0.015f * c2 - 0.02f, 0)),
            SampleFigure.Spine => Turn(4, -6 * s, -2 * s),
            SampleFigure.Head => Turn(-4 + 2 * c2, 0, 0),
            SampleFigure.UpperArmL => Turn(20 * s, 0, -4),
            SampleFigure.UpperArmR => Turn(-20 * s, 0, 4),
            SampleFigure.ForearmL => Turn(-(15 - 10 * s), 0, 0),
            SampleFigure.ForearmR => Turn(-(15 + 10 * s), 0, 0),
            SampleFigure.HandL or SampleFigure.HandR => Turn(-5, 0, 0),
            SampleFigure.ThighL => Turn(thighL, 0, 0),
            SampleFigure.ShinL => Turn(kneeL, 0, 0),
            SampleFigure.FootL => Turn(-0.6f * (thighL + kneeL), 0, 0),
            SampleFigure.ThighR => Turn(thighR, 0, 0),
            SampleFigure.ShinR => Turn(kneeR, 0, 0),
            SampleFigure.FootR => Turn(-0.6f * (thighR + kneeR), 0, 0),
            _ => default,
        };
    });

    // ── Wave ──────────────────────────────────────────────────────────────────

    /// <summary>Ease bytes of the wave: a slow start and stop on the raise and lower, a softer one on each swing.</summary>
    private const sbyte Slow = 64, Soft = 32;

    /// <summary>
    /// The right arm rises (frames 0-12), waves twice (12-52) and comes back down (52-64). Every key is
    /// eased. It is meant to play as an action: the arm's bones have weight 10 (the action replaces
    /// whatever state is playing there) and every other bone weight 0 (left to the state), with
    /// 480/640-tick ramps.
    /// </summary>
    public static RfaClip Wave()
    {
        int end = Frame(64);
        var tracks = new RfaBoneTrack[BoneCount];
        for (int b = 0; b < BoneCount; b++) tracks[b] = Hold(b, end, weight: 0f);

        // (frame, forearm roll, wrist roll, ease in, ease out)
        (int Frame, float Forearm, float Wrist, sbyte In, sbyte Out)[] swing =
        [
            (0, 0, 0, 0, Slow),
            (12, 70, 0, Slow, Soft),
            (20, 45, -15, Soft, Soft),
            (28, 95, 15, Soft, Soft),
            (36, 45, -15, Soft, Soft),
            (44, 95, 15, Soft, Soft),
            (52, 70, 0, Soft, Slow),
            (64, 0, 0, Slow, 0),
        ];
        var raised = new Vector3(-15, 0, 100); // out to the side (+X), a little forward
        tracks[SampleFigure.UpperArmR] = new RfaBoneTrack(ClipBlender.FullWeight,
            Rotations([(Frame(0), Vector3.Zero, 0, Slow), (Frame(12), raised, Slow, Soft), (Frame(52), raised, Soft, Slow), (Frame(64), Vector3.Zero, Slow, 0)]),
            tracks[SampleFigure.UpperArmR].PositionKeys);
        tracks[SampleFigure.ForearmR] = new RfaBoneTrack(ClipBlender.FullWeight,
            Rotations([.. swing.Select(k => (Frame(k.Frame), new Vector3(0, 0, k.Forearm), k.In, k.Out))]),
            tracks[SampleFigure.ForearmR].PositionKeys);
        tracks[SampleFigure.HandR] = new RfaBoneTrack(ClipBlender.FullWeight,
            Rotations([.. swing.Select(k => (Frame(k.Frame), new Vector3(0, 0, k.Wrist), k.In, k.Out))]),
            tracks[SampleFigure.HandR].PositionKeys);

        return new RfaClip { StartTime = Start, EndTime = end, RampIn = 480, RampOut = 640, Bones = [.. tracks] };
    }

    // ── Broken ────────────────────────────────────────────────────────────────

    /// <summary>
    /// What <see cref="Broken"/> gets wrong, rule by rule, in the words the README uses. RFA023 needs
    /// the library (or a reference clip) to fire; the others need at most the preview mesh.
    /// </summary>
    public static ImmutableArray<(string Code, string What)> BrokenRules { get; } =
    [
        (ClipRules.NoPositionKeys, "`hand_l` has no position keys, so it collapses onto its parent's joint (the elbow)."),
        (ClipRules.KeyTimesNotIncreasing, "`shin_r` has two rotation keys at the same time (tick 2560, halfway through)."),
        (ClipRules.KeyOutsideRange, "`head` has a rotation key two frames after the clip's end."),
        (ClipRules.ZeroQuaternion, "`hand_r`'s last rotation key is all zeros."),
        (ClipRules.NoRotationKeys, "`foot_l` has no rotation keys."),
        (ClipRules.ZeroControlPoints, "`thigh_l`'s position keys have Bezier control points left at (0, 0, 0)."),
        (ClipRules.NonUnitQuaternion, "`spine`'s last rotation key is stored 0.9 long."),
        (ClipRules.RampsLongerThanClip, "The ramps (2400 in + 2880 out ticks) are longer than the one-second clip (4800 ticks)."),
        (ClipRules.ProportionsDiffer, "`forearm_l` is half as long again as in the other clips (needs the library: it compares with the idle)."),
        (ClipRules.WeightAboveTen, "`pelvis` has weight 12."),
        (ClipRules.SegmentSnaps, "`upper_arm_r`'s two keys are stored slightly longer than 1 and 0.1 degrees apart, so the game's slerp snaps instead of turning."),
        (ClipRules.SignDiscontinuity, "`upper_arm_l`'s middle key is stored negated (the same rotation, the other hemisphere)."),
        (ClipRules.NonZeroPad, "`forearm_r`'s last rotation key has a non-zero pad word."),
    ];

    /// <summary>
    /// A one-second clip of the sample figure that is wrong in thirteen different ways
    /// (<see cref="BrokenRules"/>) but can still be written and read: open it to see the Problems
    /// panel and its quick fixes at work.
    /// </summary>
    public static RfaClip Broken()
    {
        int end = Frame(30);
        var tracks = new RfaBoneTrack[BoneCount];
        for (int b = 0; b < BoneCount; b++) tracks[b] = Hold(b, end, weight: 1f);
        var identity = ClipEdit.QuantizeRotation(Start, Quaternion.Identity, null);

        // RFA025: above 10, the engine's state factor goes negative.
        tracks[SampleFigure.Pelvis] = tracks[SampleFigure.Pelvis] with { Weight = 12f };

        // RFA021: a key 0.9 long.
        tracks[SampleFigure.Spine] = tracks[SampleFigure.Spine] with
        {
            RotationKeys = [identity, new RfaRotKey(end, 0, 0, 0, (short)Math.Round(0.9 * RfaClip.QuaternionScale))],
        };

        // RFA006: a key after the end.
        tracks[SampleFigure.Head] = tracks[SampleFigure.Head] with
        {
            RotationKeys = Rotations([(Start, Vector3.Zero, 0, 0), (Frame(15), new Vector3(0, 20, 0), 0, 0), (end + 2 * RfaClip.TicksPerFrame, Vector3.Zero, 0, 0)]),
        };

        // RFA030: the middle key in the other hemisphere.
        var armL = Rotations([(Start, new Vector3(0, 0, -10), 0, 0), (Frame(15), new Vector3(0, 0, -25), 0, 0), (end, new Vector3(0, 0, -10), 0, 0)]);
        var flipped = armL[1] with { X = (short)-armL[1].X, Y = (short)-armL[1].Y, Z = (short)-armL[1].Z, W = (short)-armL[1].W };
        tracks[SampleFigure.UpperArmL] = tracks[SampleFigure.UpperArmL] with { RotationKeys = armL.SetItem(1, flipped) };

        // RFA023: a forearm half as long again.
        var forearm = Rest[SampleFigure.ForearmL].Position * 1.5f;
        tracks[SampleFigure.ForearmL] = tracks[SampleFigure.ForearmL] with { PositionKeys = Positions([(Start, forearm), (end, forearm)], loop: false) };

        // RFA003: no position keys.
        tracks[SampleFigure.HandL] = tracks[SampleFigure.HandL] with { PositionKeys = [] };

        // RFA028: both keys stored 16384 long (just over 1) and 0.1 degrees apart: the raw dot product
        // is within 1e-6 of 1, so the game's slerp returns the second key for the whole segment.
        tracks[SampleFigure.UpperArmR] = tracks[SampleFigure.UpperArmR] with
        {
            RotationKeys = [new RfaRotKey(Start, 0, 0, 0, 16384), new RfaRotKey(end, 14, 0, 0, 16384)],
        };

        // RFA031: a pad word that is not 0.
        tracks[SampleFigure.ForearmR] = tracks[SampleFigure.ForearmR] with
        {
            RotationKeys = [identity, identity with { Time = end, Pad = 7 }],
        };

        // RFA012: an all-zero rotation.
        tracks[SampleFigure.HandR] = tracks[SampleFigure.HandR] with { RotationKeys = [identity, new RfaRotKey(end, 0, 0, 0, 0)] };

        // RFA020: control points at the origin.
        var hip = Round(Rest[SampleFigure.ThighL].Position);
        tracks[SampleFigure.ThighL] = tracks[SampleFigure.ThighL] with
        {
            PositionKeys = [new RfaPosKey(Start, hip, hip, Vector3.Zero), new RfaPosKey(end, hip, Vector3.Zero, hip)],
        };

        // RFA004: no rotation keys.
        tracks[SampleFigure.FootL] = tracks[SampleFigure.FootL] with { RotationKeys = [] };

        // RFA005: two keys at the same time.
        tracks[SampleFigure.ShinR] = tracks[SampleFigure.ShinR] with
        {
            RotationKeys = Rotations([(Start, Vector3.Zero, 0, 0), (Frame(15), new Vector3(10, 0, 0), 0, 0), (Frame(15), new Vector3(20, 0, 0), 0, 0), (end, Vector3.Zero, 0, 0)]),
        };

        // RFA022: ramps longer than the clip.
        return new RfaClip { StartTime = Start, EndTime = end, RampIn = 2400, RampOut = 2880, Bones = [.. tracks] };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BonePose Turn(float pitch, float yaw, float roll) => new(new Vector3(pitch, yaw, roll), Vector3.Zero);

    private static float Sin(double radians) => (float)Math.Sin(radians);

    private static float Cos(double radians) => (float)Math.Cos(radians);

    /// <summary>
    /// A looping clip of <paramref name="frames"/> frames with <paramref name="keys"/> evenly spaced
    /// keys plus a closing key equal to the first. A bone whose pose never changes gets just a start
    /// and an end key of each kind; the others get every key, with Catmull-Rom control points that wrap
    /// around the loop so the seam is smooth.
    /// </summary>
    private static RfaClip Loop(int frames, int keys, float weight, Func<int, double, BonePose> pose)
    {
        int end = Frame(frames);
        var tracks = new RfaBoneTrack[BoneCount];
        for (int b = 0; b < BoneCount; b++)
        {
            var poses = new BonePose[keys + 1];
            var times = new int[keys + 1];
            for (int k = 0; k <= keys; k++)
            {
                // k % keys makes the last key bit-identical to the first.
                poses[k] = pose(b, 2 * Math.PI * (k % keys) / keys);
                times[k] = Start + (end - Start) * k / keys;
            }

            bool turns = poses.Any(p => p.Euler != poses[0].Euler);
            bool moves = poses.Any(p => p.Offset != poses[0].Offset);
            var rot = turns
                ? Rotations([.. poses.Select((p, k) => (times[k], p.Euler, (sbyte)0, (sbyte)0))])
                : Rotations([(Start, poses[0].Euler, 0, 0), (end, poses[0].Euler, 0, 0)]);
            var rest = Rest[b].Position;
            var pos = moves
                ? Positions([.. poses.Select((p, k) => (times[k], rest + p.Offset))], loop: true)
                : Positions([(Start, rest + poses[0].Offset), (end, rest + poses[0].Offset)], loop: true);
            tracks[b] = new RfaBoneTrack(weight, rot, pos);
        }
        return new RfaClip { StartTime = Start, EndTime = end, Bones = [.. tracks] };
    }

    /// <summary>A bone held in its bind pose: two identity rotation keys and two rest position keys.</summary>
    private static RfaBoneTrack Hold(int bone, int end, float weight) => new(weight,
        Rotations([(Start, Vector3.Zero, 0, 0), (end, Vector3.Zero, 0, 0)]),
        Positions([(Start, Rest[bone].Position), (end, Rest[bone].Position)], loop: false));

    /// <summary>Rotation keys from Euler angles (<see cref="Quat.FromEulerDegrees"/>), each sign-continuous with the one before.</summary>
    private static ImmutableArray<RfaRotKey> Rotations(IReadOnlyList<(int Time, Vector3 Euler, sbyte EaseIn, sbyte EaseOut)> keys)
    {
        var result = new List<RfaRotKey>(keys.Count);
        foreach (var (time, euler, easeIn, easeOut) in keys)
            result.Add(ClipEdit.QuantizeRotation(time, Quat.FromEulerDegrees(euler), result.Count > 0 ? result[^1] : null, easeIn, easeOut));
        return [.. result];
    }

    /// <summary>
    /// Position keys with Catmull-Rom control points (a third of the way along the tangent through the
    /// neighbours, scaled to each segment's length). At the ends of a non-looping track the missing
    /// neighbour is the key itself; a looping track's first and last keys see across the seam. A
    /// constant track gets control points equal to its position, as the stock exporter writes.
    /// </summary>
    private static ImmutableArray<RfaPosKey> Positions(IReadOnlyList<(int Time, Vector3 Position)> keys, bool loop)
    {
        int n = keys.Count;
        int period = keys[n - 1].Time - keys[0].Time;
        var result = new RfaPosKey[n];
        for (int i = 0; i < n; i++)
        {
            var (time, p) = keys[i];
            p = Round(p);
            (int Time, Vector3 Position) prev = i > 0 ? keys[i - 1] : loop && n > 2 ? (keys[n - 2].Time - period, keys[n - 2].Position) : (time, p);
            (int Time, Vector3 Position) next = i < n - 1 ? keys[i + 1] : loop && n > 2 ? (keys[1].Time + period, keys[1].Position) : (time, p);
            var slope = next.Time > prev.Time ? (Round(next.Position) - Round(prev.Position)) / (next.Time - prev.Time) : Vector3.Zero;
            var inControl = Round(p - slope * ((time - prev.Time) / 3f));
            var outControl = Round(p + slope * ((next.Time - time) / 3f));
            result[i] = new RfaPosKey(time, p, inControl, outControl);
        }
        return [.. result];
    }

    /// <summary>Rounds to 0.1 mm, so the stored floats do not depend on the maths library's last bit.</summary>
    private static Vector3 Round(Vector3 v) => new(Round(v.X), Round(v.Y), Round(v.Z));

    private static float Round(float value) => (float)(Math.Round(value * 10000.0, MidpointRounding.AwayFromZero) / 10000.0);
}
