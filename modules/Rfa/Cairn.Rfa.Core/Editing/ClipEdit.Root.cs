using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Editing;

/// <summary>Which parts of the root's motion an edit applies to. RF model space: +X right, +Y up, +Z forward.</summary>
[Flags]
public enum RootMotionAxes
{
    /// <summary>Nothing.</summary>
    None = 0,
    /// <summary>Sideways translation.</summary>
    X = 1,
    /// <summary>Vertical translation.</summary>
    Y = 2,
    /// <summary>Forward translation.</summary>
    Z = 4,
    /// <summary>Heading: the root's turn about the vertical (+Y) axis.</summary>
    Yaw = 8,
    /// <summary>Translation along the floor (X and Z).</summary>
    Horizontal = X | Z,
    /// <summary>All three translation axes.</summary>
    Translation = X | Y | Z,
    /// <summary>Every translation axis and the heading.</summary>
    All = X | Y | Z | Yaw,
}

public static partial class ClipEdit
{
    /// <summary>
    /// Makes the clip play in place: on each chosen translation axis every position key of
    /// <paramref name="rootBone"/> (point and both control points) takes the first key's value on
    /// that axis, so the root no longer travels along it. <see cref="RootMotionAxes.Yaw"/> removes the
    /// heading change: each rotation key after the first is turned about +Y (model space) so its
    /// heading (the swing-twist twist about Y) equals the first key's; tilt and roll are kept. Yaw
    /// does not touch the position keys (a turning walk keeps its curved path unless X/Z are removed
    /// too). The first keys are unchanged.
    /// </summary>
    public static RfaClip RemoveRootMotion(RfaClip clip, int rootBone, RootMotionAxes axes)
    {
        CheckBone(clip, rootBone);
        var track = clip.Bones[rootBone];
        var posKeys = track.PositionKeys;
        if ((axes & RootMotionAxes.Translation) != 0 && posKeys.Length > 0)
        {
            var first = posKeys[0].Position;
            var b = posKeys.ToBuilder();
            for (int i = 0; i < b.Count; i++)
            {
                var k = b[i];
                b[i] = new RfaPosKey(k.Time, Pin(k.Position, first, axes), Pin(k.InControl, first, axes), Pin(k.OutControl, first, axes));
            }
            posKeys = b.MoveToImmutable();
        }

        var rotKeys = track.RotationKeys;
        if ((axes & RootMotionAxes.Yaw) != 0 && rotKeys.Length > 1)
        {
            var work = new RotTrackWork(rotKeys);
            var heading0 = HeadingTwist(KeyRotation(rotKeys[0]));
            for (int i = 1; i < work.Count; i++)
            {
                var q = KeyRotation(work.Keys[i]);
                var turned = Quat.Mul(heading0, Quat.Mul(Quat.Conj(HeadingTwist(q)), q));
                if (Quat.AngleDegrees(turned, q) < 1e-4f) continue;
                work.SetRotation(i, turned);
            }
            rotKeys = work.ToImmutable();
        }
        return WithTrack(clip, rootBone, track with { RotationKeys = rotKeys, PositionKeys = posKeys });

        static Vector3 Pin(Vector3 v, Vector3 first, RootMotionAxes axes) => new(
            (axes & RootMotionAxes.X) != 0 ? first.X : v.X,
            (axes & RootMotionAxes.Y) != 0 ? first.Y : v.Y,
            (axes & RootMotionAxes.Z) != 0 ? first.Z : v.Z);
    }

    /// <summary>
    /// Scales the root's travel: every position key of <paramref name="rootBone"/> (point and both
    /// control points) becomes <c>first + (p - first) * scale</c>, relative to the first key's
    /// position. A scale of 1 on an axis leaves it as it is; 0 removes the travel on that axis.
    /// </summary>
    public static RfaClip ScaleRootMotion(RfaClip clip, int rootBone, Vector3 scale)
    {
        CheckBone(clip, rootBone);
        var track = clip.Bones[rootBone];
        var keys = track.PositionKeys;
        if (keys.Length == 0 || scale == Vector3.One) return clip;
        var first = keys[0].Position;
        var b = keys.ToBuilder();
        for (int i = 0; i < b.Count; i++)
        {
            var k = b[i];
            b[i] = new RfaPosKey(k.Time, S(k.Position), S(k.InControl), S(k.OutControl));
        }
        return WithPositionKeys(clip, rootBone, b.MoveToImmutable());

        Vector3 S(Vector3 v) => new(
            scale.X == 1f ? v.X : first.X + (v.X - first.X) * scale.X,
            scale.Y == 1f ? v.Y : first.Y + (v.Y - first.Y) * scale.Y,
            scale.Z == 1f ? v.Z : first.Z + (v.Z - first.Z) * scale.Z);
    }

    /// <summary>
    /// The skeleton's root bone: the only bone without a parent, or among several parentless bones the
    /// one named <c>root</c> (canonical name), else the one with the most descendants. -1 when empty.
    /// </summary>
    public static int FindRootBone(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        return FindRootBone(skeleton.Parents, skeleton.Names);
    }

    /// <summary>
    /// <see cref="FindRootBone(Skeleton)"/> from raw parent indices (invalid parents and cycles count as
    /// "no parent", as forward kinematics treats them) and optional bone names.
    /// </summary>
    public static int FindRootBone(IReadOnlyList<int> parents, IReadOnlyList<string>? names = null)
    {
        ArgumentNullException.ThrowIfNull(parents);
        var effective = ForwardKinematics.EffectiveParents([.. parents]);
        var roots = Enumerable.Range(0, effective.Length).Where(i => effective[i] < 0).ToList();
        if (roots.Count <= 1) return roots.Count == 0 ? -1 : roots[0];
        if (names is not null)
        {
            foreach (int r in roots)
            {
                if (r < names.Count && SkeletonMatcher.CanonicalBoneName(names[r]) is "root") return r;
            }
        }
        var descendants = new int[effective.Length];
        for (int i = 0; i < effective.Length; i++)
        {
            int p = effective[i];
            int guard = 0;
            while (p >= 0 && guard++ <= effective.Length)
            {
                descendants[p]++;
                p = effective[p];
            }
        }
        return roots.OrderByDescending(r => descendants[r]).ThenBy(r => r).First();
    }

    /// <summary>The twist of <paramref name="q"/> about +Y (heading), unit length; the identity when undefined.</summary>
    private static Quaternion HeadingTwist(Quaternion q) => Quat.Normalize(new Quaternion(0f, q.Y, 0f, q.W));
}
