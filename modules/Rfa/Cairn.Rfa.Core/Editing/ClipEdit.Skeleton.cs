using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Editing;

/// <summary>Options for <see cref="ClipEdit.ConformToSkeleton"/>.</summary>
/// <param name="UseCanonicalNames">
/// After exact (case-insensitive) name matches, also match by <see cref="SkeletonMatcher.CanonicalBoneName"/>
/// (the exporter's <c>xxxx-bdbn-</c> prefix ignored). On by default.
/// </param>
public sealed record ConformOptions(bool UseCanonicalNames = true);

/// <summary>The result of <see cref="ClipEdit.ConformToSkeleton"/>.</summary>
/// <param name="Clip">The clip with one track per target bone, in the target's order.</param>
/// <param name="SourceOfTarget">For each target bone, the source track index it came from, or -1 for a new rest-pose track.</param>
/// <param name="DroppedBones">Names of source bones the target does not have (their tracks are gone).</param>
/// <param name="AddedBones">Names of target bones the clip had no track for (given rest-pose tracks).</param>
public sealed record ConformResult(
    RfaClip Clip, ImmutableArray<int> SourceOfTarget, ImmutableArray<string> DroppedBones, ImmutableArray<string> AddedBones);

public static partial class ClipEdit
{
    /// <summary>
    /// Sets bone lengths from a reference clip of the same rig (typically its stand clip): every
    /// position key of each chosen non-root bone becomes a constant key (control points = point) at the
    /// reference clip's first position key of the same bone index. Keys are written at the bone's
    /// existing position-key times plus start and end (a track always ends up with at least those
    /// two). Root bones (no parent) are never touched: their position is the model-space motion. A bone
    /// whose reference track has no position keys is left alone.
    /// </summary>
    /// <param name="clip">The clip to change.</param>
    /// <param name="reference">A clip with the same bone list (bones match by index).</param>
    /// <param name="parents">Parent index per bone (e.g. <see cref="Skeleton.Parents"/>), to know the roots.</param>
    /// <param name="bones">Bones to change; null = every non-root bone.</param>
    public static RfaClip SetBoneLengthsFromClip(RfaClip clip, RfaClip reference, IReadOnlyList<int> parents, IEnumerable<int>? bones = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(parents);
        if (reference.BoneCount != clip.BoneCount)
            throw new ArgumentException(
                $"The reference clip has {reference.BoneCount} bones but this clip has {clip.BoneCount}; bones are matched by index, so pick a clip made for the same mesh.",
                nameof(reference));
        return SetBoneLengths(clip, parents, bones, b =>
            reference.Bones[b].PositionKeys.IsDefaultOrEmpty ? null : reference.Bones[b].PositionKeys[0].Position);
    }

    /// <summary>
    /// <see cref="SetBoneLengthsFromClip"/> with the skeleton's bind-pose local positions
    /// (<see cref="Skeleton.RestLocal"/>) as the reference.
    /// </summary>
    public static RfaClip SetBoneLengthsFromBind(RfaClip clip, Skeleton skeleton, IEnumerable<int>? bones = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(skeleton);
        PoseEditMath.CheckSkeleton(clip, skeleton, nameof(skeleton));
        return SetBoneLengths(clip, skeleton.Parents, bones, b => skeleton.RestLocal[b].Position);
    }

    private static RfaClip SetBoneLengths(RfaClip clip, IReadOnlyList<int> parents, IEnumerable<int>? bones, Func<int, Vector3?> lengthOf)
    {
        if (parents.Count != clip.BoneCount)
            throw new ArgumentException($"There are {parents.Count} parent entries for {clip.BoneCount} bones; pass the parents of the clip's mesh.", nameof(parents));
        var effective = ForwardKinematics.EffectiveParents([.. parents]);
        var result = clip.Bones.ToBuilder();
        foreach (int b in PoseEditMath.BoneList(clip, bones))
        {
            if (effective[b] < 0) continue;
            if (lengthOf(b) is not { } position) continue;
            var track = clip.Bones[b];
            var times = new SortedSet<int>(track.PositionKeys.Select(k => k.Time)) { clip.StartTime, clip.EndTime };
            var keys = times.Select(t => RfaPosKey.Constant(t, position)).ToImmutableArray();
            if (!keys.SequenceEqual(track.PositionKeys)) result[b] = track with { PositionKeys = keys };
        }
        return clip with { Bones = result.MoveToImmutable() };
    }

    /// <summary>
    /// Re-lays a clip out for another mesh's bone list, matching bones by NAME: first exact
    /// (case-insensitive), then by canonical name (<see cref="SkeletonMatcher.CanonicalBoneName"/>,
    /// unless switched off). Matched tracks move bit-identically to the target's index; source bones the
    /// target lacks are dropped; a target bone with no source gets a rest-pose track: one rotation key
    /// at start (the target's rest local rotation) and position keys at start and end (its rest local
    /// position), with the weight of its nearest mapped ancestor (or 1 when none is mapped). The header
    /// and morph data are kept. Conforming there and back (when the bone lists are reorderings of each
    /// other) returns the original tracks bit-identically.
    /// </summary>
    /// <param name="clip">The clip.</param>
    /// <param name="clipBoneNames">The clip's bone names, in its track order (from the mesh it was made for).</param>
    /// <param name="target">The target mesh's skeleton.</param>
    /// <param name="options">Matching options.</param>
    public static ConformResult ConformToSkeleton(RfaClip clip, IReadOnlyList<string> clipBoneNames, Skeleton target, ConformOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(clipBoneNames);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new ConformOptions();
        if (clipBoneNames.Count != clip.BoneCount)
            throw new ArgumentException($"There are {clipBoneNames.Count} bone names for a clip with {clip.BoneCount} bones; pass the names of the mesh this clip was made for.", nameof(clipBoneNames));

        var source = MatchBoneNames(clipBoneNames, target.Names, options.UseCanonicalNames);
        var used = new bool[clip.BoneCount];
        foreach (int s in source) if (s >= 0) used[s] = true;

        var bones = ImmutableArray.CreateBuilder<RfaBoneTrack>(target.Count);
        var added = new List<string>();
        for (int t = 0; t < target.Count; t++)
        {
            int s = source[t];
            if (s >= 0)
            {
                bones.Add(clip.Bones[s]);
                continue;
            }
            added.Add(target.Names[t]);
            var rest = target.RestLocal[t];
            float weight = 1f;
            int a = target.EffectiveParents[t];
            int guard = 0;
            while (a >= 0 && guard++ <= target.Count)
            {
                if (source[a] >= 0)
                {
                    weight = clip.Bones[source[a]].Weight;
                    break;
                }
                a = target.EffectiveParents[a];
            }
            var pos = clip.EndTime > clip.StartTime
                ? ImmutableArray.Create(RfaPosKey.Constant(clip.StartTime, rest.Position), RfaPosKey.Constant(clip.EndTime, rest.Position))
                : [RfaPosKey.Constant(clip.StartTime, rest.Position)];
            // Two rotation keys (start and end): a lone key makes the engine read past the track (RFA015).
            bones.Add(new RfaBoneTrack(weight, HoldRotation([], clip.StartTime, clip.EndTime, rest.Rotation), pos));
        }
        var dropped = Enumerable.Range(0, clip.BoneCount).Where(i => !used[i]).Select(i => clipBoneNames[i]).ToImmutableArray();
        return new ConformResult(clip with { Bones = bones.MoveToImmutable() }, [.. source], dropped, [.. added]);
    }

    /// <summary>
    /// For each target name, the index of the source name it matches, or -1: exact case-insensitive
    /// matches first (each source used once, first unused wins), then canonical-name matches among
    /// the sources still unused.
    /// </summary>
    internal static int[] MatchBoneNames(IReadOnlyList<string?> sourceNames, IReadOnlyList<string?> targetNames, bool canonical)
    {
        var result = new int[targetNames.Count];
        Array.Fill(result, -1);
        var used = new bool[sourceNames.Count];
        Pass(n => n.Trim().ToLowerInvariant());
        if (canonical) Pass(SkeletonMatcher.CanonicalBoneName);
        return result;

        void Pass(Func<string, string> key)
        {
            var index = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
            for (int s = 0; s < sourceNames.Count; s++)
            {
                if (used[s] || sourceNames[s] is not { } name) continue;
                string k = key(name);
                if (!index.TryGetValue(k, out var q)) index[k] = q = new Queue<int>();
                q.Enqueue(s);
            }
            for (int t = 0; t < targetNames.Count; t++)
            {
                if (result[t] >= 0 || targetNames[t] is not { } name) continue;
                if (index.TryGetValue(key(name), out var q) && q.Count > 0)
                {
                    int s = q.Dequeue();
                    result[t] = s;
                    used[s] = true;
                }
            }
        }
    }
}
