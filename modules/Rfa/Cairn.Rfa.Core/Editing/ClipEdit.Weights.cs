using System.Collections.Immutable;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Editing;

public static partial class ClipEdit
{
    /// <summary>
    /// The clip with the blend weight of each listed bone set to <paramref name="weight"/> (keys
    /// untouched). The weight decides which clip wins a bone when an action plays over a state
    /// (stock values: 2, 4, 5, 10); below the engine's epsilon the clip ignores the bone. Duplicates in
    /// <paramref name="bones"/> are harmless.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A bone index is not in the clip, or the weight is negative or not finite.</exception>
    public static RfaClip SetBoneWeights(RfaClip clip, IEnumerable<int> bones, float weight)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(bones);
        if (!float.IsFinite(weight) || weight < 0f)
            throw new ArgumentOutOfRangeException(nameof(weight), $"A bone weight must be a finite number of 0 or more; got {weight}.");
        var b = clip.Bones.ToBuilder();
        bool changed = false;
        foreach (int bone in bones)
        {
            CheckBone(clip, bone);
            if (b[bone].Weight.Equals(weight)) continue;
            b[bone] = b[bone] with { Weight = weight };
            changed = true;
        }
        return changed ? clip with { Bones = b.MoveToImmutable() } : clip;
    }

    /// <summary>
    /// Sets the weight of <paramref name="rootBone"/>'s whole subtree (every bone whose parent chain
    /// reaches it), optionally excluding the root itself ("apply to children").
    /// </summary>
    /// <param name="clip">The clip.</param>
    /// <param name="parents">Parent index per bone (-1 for roots), e.g. <see cref="Skeleton.Parents"/> of the preview mesh. Bones beyond the list are treated as roots; parent cycles are tolerated.</param>
    /// <param name="rootBone">The subtree's root.</param>
    /// <param name="weight">The weight to set.</param>
    /// <param name="includeRoot">False to set only the descendants.</param>
    public static RfaClip SetBoneWeightsForSubtree(RfaClip clip, IReadOnlyList<int> parents, int rootBone, float weight, bool includeRoot = true)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(parents);
        CheckBone(clip, rootBone);
        var bones = WeightPreset.Subtree(parents, rootBone, includeRoot).Where(i => i < clip.BoneCount);
        return SetBoneWeights(clip, bones, weight);
    }
}

/// <summary>
/// Bone sets for the weight presets (all / upper body / lower body), found from a skeleton's names
/// and hierarchy.
/// </summary>
/// <remarks>
/// The heuristic, in order:
/// <list type="number">
/// <item><b>Upper body</b>: the bone whose name contains "spine" (case-insensitive; then "chest", then
/// "torso") with the smallest hierarchy depth (ties: lowest index), and its whole subtree. On the stock
/// rigs that is spine03 (<c>tech- 1spine</c> on the civilian rig) with spine04, head, arms, hands,
/// fingers and the merc shoulder pads.</item>
/// <item><b>Lower body</b> (with an upper body found): the ancestors of the upper-body root, every
/// root bone, and the subtrees of every bone whose name contains "pelvis", "hip", "leg" or "thigh",
/// minus the upper body. On the stock rigs: root, pelvis, legs, feet and toes.</item>
/// <item><b>Fallback</b> (no spine-like bone): by name only. Upper: names containing spine, chest,
/// torso, neck, head, clavicle, shoulder, arm, hand, finger or thumb. Lower: names containing root,
/// pelvis, hip, leg, thigh, knee, calf, shin, foot or toe (and not in the upper set).</item>
/// </list>
/// Bones matching neither (props, tails) are in neither set. Results are ascending bone indices.
/// </remarks>
public static class WeightPreset
{
    private static readonly string[] SpineNames = ["spine", "chest", "torso"];
    private static readonly string[] LowerRootNames = ["pelvis", "hip", "leg", "thigh"];
    private static readonly string[] FallbackUpper = ["spine", "chest", "torso", "neck", "head", "clavicle", "shoulder", "arm", "hand", "finger", "thumb"];
    private static readonly string[] FallbackLower = ["root", "pelvis", "hip", "leg", "thigh", "knee", "calf", "shin", "foot", "toe"];

    /// <summary>Every bone index 0..<paramref name="boneCount"/>-1.</summary>
    public static ImmutableArray<int> All(int boneCount) => [.. Enumerable.Range(0, Math.Max(0, boneCount))];

    /// <summary>The upper-body bones of a skeleton (see the class remarks).</summary>
    public static ImmutableArray<int> UpperBody(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        return UpperBody(skeleton.Parents, skeleton.Names);
    }

    /// <summary>The lower-body bones of a skeleton (see the class remarks).</summary>
    public static ImmutableArray<int> LowerBody(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        return LowerBody(skeleton.Parents, skeleton.Names);
    }

    /// <summary>The upper-body bones from parent indices and names (equal lengths).</summary>
    public static ImmutableArray<int> UpperBody(IReadOnlyList<int> parents, IReadOnlyList<string> names)
    {
        Check(parents, names);
        int spine = FindSpine(parents, names);
        if (spine >= 0) return Subtree(parents, spine, true);
        return [.. Enumerable.Range(0, names.Count).Where(i => Matches(names[i], FallbackUpper))];
    }

    /// <summary>The lower-body bones from parent indices and names (equal lengths).</summary>
    public static ImmutableArray<int> LowerBody(IReadOnlyList<int> parents, IReadOnlyList<string> names)
    {
        Check(parents, names);
        var upper = new HashSet<int>(UpperBody(parents, names));
        int spine = FindSpine(parents, names);
        var lower = new SortedSet<int>();
        if (spine >= 0)
        {
            int p = Parent(parents, spine);
            for (int guard = 0; p >= 0 && guard < parents.Count; guard++, p = Parent(parents, p)) lower.Add(p);
            for (int i = 0; i < names.Count; i++)
            {
                if (Parent(parents, i) < 0) lower.Add(i);
                if (Matches(names[i], LowerRootNames)) lower.UnionWith(Subtree(parents, i, true));
            }
        }
        else
        {
            for (int i = 0; i < names.Count; i++)
            {
                if (Matches(names[i], FallbackLower)) lower.Add(i);
            }
        }
        lower.ExceptWith(upper);
        return [.. lower];
    }

    /// <summary>
    /// <paramref name="root"/>'s subtree: every bone whose parent chain reaches it (cycles and
    /// out-of-range parents end a chain), with or without the root, ascending.
    /// </summary>
    public static ImmutableArray<int> Subtree(IReadOnlyList<int> parents, int root, bool includeRoot)
    {
        ArgumentNullException.ThrowIfNull(parents);
        var result = new List<int>();
        for (int i = 0; i < parents.Count; i++)
        {
            if (i == root)
            {
                if (includeRoot) result.Add(i);
                continue;
            }
            int p = Parent(parents, i);
            for (int guard = 0; p >= 0 && guard < parents.Count; guard++, p = Parent(parents, p))
            {
                if (p == root)
                {
                    result.Add(i);
                    break;
                }
            }
        }
        if (includeRoot && root >= parents.Count && root >= 0) result.Add(root);
        return [.. result];
    }

    private static int FindSpine(IReadOnlyList<int> parents, IReadOnlyList<string> names)
    {
        foreach (string word in SpineNames)
        {
            int best = -1, bestDepth = int.MaxValue;
            for (int i = 0; i < names.Count; i++)
            {
                if (!names[i].Contains(word, StringComparison.OrdinalIgnoreCase)) continue;
                int depth = Depth(parents, i);
                if (depth < bestDepth) (best, bestDepth) = (i, depth);
            }
            if (best >= 0) return best;
        }
        return -1;
    }

    private static int Depth(IReadOnlyList<int> parents, int i)
    {
        int d = 0;
        for (int p = Parent(parents, i); p >= 0 && d < parents.Count; p = Parent(parents, p)) d++;
        return d;
    }

    private static int Parent(IReadOnlyList<int> parents, int i)
    {
        if ((uint)i >= (uint)parents.Count) return -1;
        int p = parents[i];
        return (uint)p < (uint)parents.Count && p != i ? p : -1;
    }

    private static bool Matches(string name, string[] words) =>
        words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static void Check(IReadOnlyList<int> parents, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(names);
        if (parents.Count != names.Count)
            throw new ArgumentException($"There are {parents.Count} parent indices but {names.Count} names; pass one of each per bone.", nameof(names));
    }
}
