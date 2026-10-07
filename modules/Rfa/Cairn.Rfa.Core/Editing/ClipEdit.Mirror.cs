using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Editing;

/// <summary>The model-space axis a mirror flips (the mirror plane is perpendicular to it).</summary>
public enum MirrorAxis
{
    /// <summary>Flip X: mirror across the YZ plane (left/right; RF's left bones sit at negative X).</summary>
    X,
    /// <summary>Flip Y: mirror across the XZ plane (up/down).</summary>
    Y,
    /// <summary>Flip Z: mirror across the XY plane (front/back).</summary>
    Z,
}

/// <summary>How <see cref="ClipEdit.MirrorClip"/> (and mirrored pastes) mirror a pose.</summary>
/// <param name="Axis">The axis to flip; X (left/right) by default.</param>
/// <param name="Skeleton">
/// The clip's mesh skeleton. With it, each bone's model-space change from its rest pose is mirrored,
/// which is right for rigs whose left and right bone frames are not exact reflections of each other.
/// Without it, local rotations and positions are reflected directly.
/// </param>
public sealed record MirrorOptions(MirrorAxis Axis = MirrorAxis.X, Skeleton? Skeleton = null);

/// <summary>
/// The per-bone constants of a mirror. For a destination bone d with partner p, the mirrored local
/// rotation is <c>Pre(d) * reflect(L(p)) * Post(d)</c> with <c>Post(d) = C(d)</c>,
/// <c>Pre(d) = C(parent(d))^-1</c> and <c>C(j) = reflect(Wrest(partner j)^-1) * Wrest(j)</c> (rest
/// world rotations). That is exactly "mirror the model-space delta from rest", <c>W'(d) =
/// reflect(W(p) * Wrest(p)^-1) * Wrest(d)</c>, whenever the hierarchy is symmetric at d
/// (<c>parent(partner d) == partner(parent d)</c>): the parent chain cancels, so the mirrored local
/// depends only on the partner's local at the same time, and the partner's key times and eases can be
/// kept (slerp commutes with the constant products and the reflection). Without a skeleton every C
/// is the identity.
/// </summary>
internal sealed class MirrorFrames
{
    public MirrorFrames(BonePairMap pairs, MirrorOptions options, int boneCount)
    {
        Pairs = pairs;
        Axis = options.Axis;
        Skeleton = options.Skeleton;
        Pre = new Quaternion[boneCount];
        Post = new Quaternion[boneCount];
        Symmetric = new bool[boneCount];
        Exact = new bool[boneCount];
        Array.Fill(Pre, Quaternion.Identity);
        Array.Fill(Post, Quaternion.Identity);
        Array.Fill(Symmetric, true);
        Array.Fill(Exact, true);
        if (Skeleton is null) return;

        var parents = Skeleton.EffectiveParents;
        var c = new Quaternion[boneCount];
        for (int j = 0; j < boneCount; j++)
        {
            int p = pairs.Partner[j];
            c[j] = Quat.Normalize(Quat.Mul(Reflect(Quat.Conj(Skeleton.RestWorld[p].Rotation), Axis), Skeleton.RestWorld[j].Rotation));
        }
        for (int d = 0; d < boneCount; d++)
        {
            int parent = parents[d];
            int partner = pairs.Partner[d];
            int partnersParent = parents[partner];
            int parentsPartner = parent < 0 ? -1 : pairs.Partner[parent];
            Symmetric[d] = partnersParent == parentsPartner;
            Post[d] = c[d];
            Pre[d] = parent < 0 ? Quaternion.Identity : Quat.Conj(c[parent]);
            Exact[d] = NearIdentity(Pre[d]) && NearIdentity(Post[d]);
        }

        // Vector part under 1e-5 is about 0.001 degrees, far below one int16 step (about 0.007).
        static bool NearIdentity(Quaternion q) => MathF.Abs(q.X) + MathF.Abs(q.Y) + MathF.Abs(q.Z) < 1e-5f;
    }

    public BonePairMap Pairs { get; }

    public MirrorAxis Axis { get; }

    public Skeleton? Skeleton { get; }

    public Quaternion[] Pre { get; }

    public Quaternion[] Post { get; }

    /// <summary>The hierarchy is symmetric at this bone (the partner's keys can be mapped one to one).</summary>
    public bool[] Symmetric { get; }

    /// <summary>Pre and Post are the identity: rotation keys map by exact integer sign flips.</summary>
    public bool[] Exact { get; }

    /// <summary>The reflection of a rotation: the component along the flipped axis is kept, the other two vector components negate.</summary>
    public static Quaternion Reflect(Quaternion q, MirrorAxis axis) => axis switch
    {
        MirrorAxis.X => new Quaternion(q.X, -q.Y, -q.Z, q.W),
        MirrorAxis.Y => new Quaternion(-q.X, q.Y, -q.Z, q.W),
        _ => new Quaternion(-q.X, -q.Y, q.Z, q.W),
    };

    /// <summary>The reflection of a point: the component along the flipped axis negates.</summary>
    public static Vector3 Reflect(Vector3 v, MirrorAxis axis) => axis switch
    {
        MirrorAxis.X => new Vector3(-v.X, v.Y, v.Z),
        MirrorAxis.Y => new Vector3(v.X, -v.Y, v.Z),
        _ => new Vector3(v.X, v.Y, -v.Z),
    };

    /// <summary>
    /// Bone <paramref name="dest"/>'s mirrored rotation keys made from its partner's
    /// <paramref name="source"/> keys: same times, eases and pad. Exact bones flip the stored int16
    /// signs (the file quaternion reflects the same way as the active one), so nothing is re-quantised
    /// and mirroring twice gives the original bits; other bones are re-quantised once.
    /// </summary>
    public ImmutableArray<RfaRotKey> MapRotationKeys(int dest, ImmutableArray<RfaRotKey> source)
    {
        if (source.IsDefaultOrEmpty) return source.IsDefault ? [] : source;
        var result = new RfaRotKey[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            var k = source[i];
            if (Exact[dest])
            {
                result[i] = Axis switch
                {
                    MirrorAxis.X => k with { Y = Neg(k.Y), Z = Neg(k.Z) },
                    MirrorAxis.Y => k with { X = Neg(k.X), Z = Neg(k.Z) },
                    _ => k with { X = Neg(k.X), Y = Neg(k.Y) },
                };
            }
            else
            {
                var active = Quat.Mul(Pre[dest], Quat.Mul(Reflect(ClipEdit.KeyRotation(k), Axis), Post[dest]));
                result[i] = ClipEdit.QuantizeRotation(k.Time, active, i > 0 ? result[i - 1] : null, k.EaseIn, k.EaseOut) with { Pad = k.Pad };
            }
        }
        return [.. result];

        static short Neg(short c) => c == short.MinValue ? short.MaxValue : (short)-c;
    }

    /// <summary>
    /// Bone <paramref name="dest"/>'s mirrored position keys made from its partner's: each point and
    /// control point is reflected, turned by <c>Pre(dest)</c> into the destination's parent frame and
    /// scaled by <paramref name="scale"/> (the bone-length ratio); times are kept.
    /// </summary>
    public ImmutableArray<RfaPosKey> MapPositionKeys(int dest, ImmutableArray<RfaPosKey> source, float scale)
    {
        if (source.IsDefaultOrEmpty) return source.IsDefault ? [] : source;
        var pre = Pre[dest];
        bool turn = pre != Quaternion.Identity;
        var result = new RfaPosKey[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            var k = source[i];
            result[i] = new RfaPosKey(k.Time, Map(k.Position), Map(k.InControl), Map(k.OutControl));
        }
        return [.. result];

        Vector3 Map(Vector3 v)
        {
            var r = Reflect(v, Axis);
            if (turn) r = Quat.Rotate(pre, r);
            return scale == 1f ? r : r * scale;
        }
    }

    /// <summary>
    /// The factor that keeps a bone's <paramref name="own"/> length when it takes its partner's
    /// position track: <c>|own first key| / |partner first key|</c>, or 1 for an unpaired bone, a
    /// missing track or a near-zero length.
    /// </summary>
    public static float LengthScale(ImmutableArray<RfaPosKey> own, ImmutableArray<RfaPosKey> partner)
    {
        if (own.IsDefaultOrEmpty || partner.IsDefaultOrEmpty) return 1f;
        float a = own[0].Position.Length(), b = partner[0].Position.Length();
        if (a < 1e-6f || b < 1e-6f || a == b) return 1f;
        return a / b;
    }
}

public static partial class ClipEdit
{
    /// <summary>
    /// Mirrors a clip: every bone takes its partner's animation, reflected across the plane
    /// perpendicular to <see cref="MirrorOptions.Axis"/>; centre bones (their own partner) are reflected
    /// in place. Weights move with the tracks (a right-arm action becomes a left-arm one).
    /// <list type="bullet">
    /// <item><b>Rotations.</b> Without a skeleton the locals are reflected directly (plane YZ:
    /// <c>(x, y, z, w) -&gt; (x, -y, -z, w)</c>) by exact int16 sign flips. With a skeleton each bone's
    /// MODEL-SPACE delta from its rest pose is mirrored (<c>W'(i) = reflect(W(p) * Wrest(p)^-1) *
    /// Wrest(i)</c>, see <see cref="MirrorFrames"/>): where the hierarchy is symmetric (always in the
    /// stock rigs) this is a constant product on the partner's local, so the partner's key times,
    /// eases and pad are KEPT and each key is re-quantised once. A bone where the hierarchy is not
    /// symmetric is resampled instead: keys at the union of the rotation key times of the partner's
    /// chain and the parent's partner's chain, eases 0, converted back to a local key by key.</item>
    /// <item><b>Positions.</b> Each bone takes its partner's position keys (times kept), reflected and
    /// turned into its own parent's frame, then scaled so the bone keeps its OWN length (the ratio of
    /// the first keys' lengths), because bone lengths belong to the rig, not to the side. The root
    /// (and any centre bone) is simply reflected, so the root's travel mirrors across the plane.
    /// Resampled bones keep their own position keys.</item>
    /// </list>
    /// Mirroring twice gives back the original pose (bit-identical without a skeleton, within
    /// quantisation with one), and a symmetric pose stays symmetric.
    /// </summary>
    /// <exception cref="ArgumentException">The pair map or the skeleton does not fit the clip.</exception>
    public static RfaClip MirrorClip(RfaClip clip, BonePairMap pairs, MirrorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(pairs);
        options ??= new MirrorOptions();
        pairs.Validate(clip.BoneCount);
        if (options.Skeleton is { } sk) PoseEditMath.CheckSkeleton(clip, sk, nameof(options));
        var frames = new MirrorFrames(pairs, options, clip.BoneCount);

        var bones = ImmutableArray.CreateBuilder<RfaBoneTrack>(clip.BoneCount);
        for (int d = 0; d < clip.BoneCount; d++)
        {
            int p = pairs.Partner[d];
            var src = clip.Bones[p];
            var own = clip.Bones[d];
            if (frames.Skeleton is null || frames.Symmetric[d])
            {
                float scale = p == d ? 1f : MirrorFrames.LengthScale(own.PositionKeys, src.PositionKeys);
                bones.Add(new RfaBoneTrack(src.Weight,
                    frames.MapRotationKeys(d, src.RotationKeys),
                    frames.MapPositionKeys(d, src.PositionKeys, scale)));
            }
            else
            {
                bones.Add(new RfaBoneTrack(src.Weight, MirrorResampled(clip, frames, d), own.PositionKeys));
            }
        }
        return clip with { Bones = bones.MoveToImmutable() };
    }

    private static ImmutableArray<RfaRotKey> MirrorResampled(RfaClip clip, MirrorFrames frames, int dest)
    {
        var skeleton = frames.Skeleton!;
        var parents = skeleton.EffectiveParents;
        var pairs = frames.Pairs;
        var times = new SortedSet<int>();
        void AddChain(int bone)
        {
            int guard = 0;
            while (bone >= 0 && guard++ <= skeleton.Count)
            {
                foreach (var k in clip.Bones[bone].RotationKeys) times.Add(k.Time);
                bone = parents[bone];
            }
        }
        int parent = parents[dest];
        AddChain(pairs.Partner[dest]);
        if (parent >= 0) AddChain(pairs.Partner[parent]);
        if (times.Count == 0) times.Add(clip.StartTime);

        var world = new Quaternion[skeleton.Count];
        var keys = new List<RfaRotKey>(times.Count);
        foreach (int t in times)
        {
            PoseEditMath.WorldRotations(clip, skeleton, t, world);
            var w = MirroredWorld(dest);
            var local = parent < 0 ? w : Quat.Mul(Quat.Conj(MirroredWorld(parent)), w);
            keys.Add(QuantizeRotation(t, local, keys.Count > 0 ? keys[^1] : null));
        }
        return [.. keys];

        Quaternion MirroredWorld(int j)
        {
            int p = pairs.Partner[j];
            var delta = Quat.Mul(world[p], Quat.Conj(skeleton.RestWorld[p].Rotation));
            return Quat.Normalize(Quat.Mul(MirrorFrames.Reflect(delta, frames.Axis), skeleton.RestWorld[j].Rotation));
        }
    }
}
