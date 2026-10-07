using Cairn.Rfa.Animation;

namespace Cairn.Rfa.Retarget;

/// <summary>Which steps <see cref="BoneMapper"/> may use.</summary>
public sealed record BoneMapOptions
{
    /// <summary>The defaults: every step on, fuzzy threshold 0.6.</summary>
    public static BoneMapOptions Default { get; } = new();

    /// <summary>Step 1: identical names (case-insensitive).</summary>
    public bool ExactNames { get; init; } = true;

    /// <summary>Step 2: identical canonical names, each side through its own profile.</summary>
    public bool CanonicalNames { get; init; } = true;

    /// <summary>Step 3: identical names under the built-in stock rules.</summary>
    public bool BuiltInTables { get; init; } = true;

    /// <summary>Step 4: similar body words.</summary>
    public bool Fuzzy { get; init; } = true;

    /// <summary>The lowest word similarity (0..1, Dice coefficient) the fuzzy step accepts.</summary>
    public double FuzzyThreshold { get; init; } = 0.6;
}

/// <summary>
/// Maps every TARGET bone to a SOURCE bone (or none). Steps, in order, for each target bone still
/// unmapped:
/// <list type="number">
/// <item>exact name, case-insensitive;</item>
/// <item>canonical name: the target name through the target profile equals a source name through
/// the source profile;</item>
/// <item>built-in tables: both names through the stock rules (the generic <c>xxxx-bdbn-</c> prefix,
/// the stock prefixes, the civilian table) — the four stock rigs' canonical names already line up,
/// so this catches stock rigs paired with a custom profile;</item>
/// <item>fuzzy, deliberately conservative: names are split into body words (<c>BoneTokens</c>:
/// upper arm, thigh = upper leg, ...), the left/right sides must agree, the similarity is the Dice
/// coefficient of the word lists, and a pairing is made only when it is at least
/// <see cref="BoneMapOptions.FuzzyThreshold"/>, is the target's unique best, and is the source's
/// unique best among the remaining targets. The fuzzy step only uses source bones no earlier step
/// used, so it never puts two target bones on one source bone.</item>
/// </list>
/// Steps 1-3 take the first source bone with the matching name even if another target already uses
/// it (two target bones on one source bone are then reported by <see cref="BoneMap.Validate"/>).
/// </summary>
public static class BoneMapper
{
    /// <summary>Maps <paramref name="target"/>'s bones onto <paramref name="source"/>'s.</summary>
    public static BoneMap Map(Skeleton source, RigProfile sourceProfile, Skeleton target, RigProfile targetProfile, BoneMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return Map(source.Names, source.Parents, sourceProfile, target.Names, target.Parents, targetProfile, options);
    }

    /// <summary>Maps a target bone list onto a source bone list (names and parent indices).</summary>
    public static BoneMap Map(
        IReadOnlyList<string> sourceNames, IReadOnlyList<int> sourceParents, RigProfile sourceProfile,
        IReadOnlyList<string> targetNames, IReadOnlyList<int> targetParents, RigProfile targetProfile,
        BoneMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sourceNames);
        ArgumentNullException.ThrowIfNull(sourceParents);
        ArgumentNullException.ThrowIfNull(sourceProfile);
        ArgumentNullException.ThrowIfNull(targetNames);
        ArgumentNullException.ThrowIfNull(targetParents);
        ArgumentNullException.ThrowIfNull(targetProfile);
        if (sourceParents.Count != sourceNames.Count) throw new ArgumentException("Every source bone needs a parent index.", nameof(sourceParents));
        if (targetParents.Count != targetNames.Count) throw new ArgumentException("Every target bone needs a parent index.", nameof(targetParents));
        options ??= BoneMapOptions.Default;

        int ns = sourceNames.Count, nt = targetNames.Count;
        var map = Enumerable.Repeat(-1, nt).ToArray();
        var kinds = new BoneMatchKind[nt];

        void Step(BoneMatchKind kind, Func<string, string> sourceKey, Func<string, string> targetKey, StringComparer comparer)
        {
            var index = new Dictionary<string, int>(comparer);
            for (int s = 0; s < ns; s++) index.TryAdd(sourceKey(sourceNames[s]), s);
            for (int t = 0; t < nt; t++)
            {
                if (map[t] >= 0) continue;
                if (index.TryGetValue(targetKey(targetNames[t]), out int s))
                {
                    map[t] = s;
                    kinds[t] = kind;
                }
            }
        }

        if (options.ExactNames) Step(BoneMatchKind.ExactName, n => n, n => n, StringComparer.OrdinalIgnoreCase);
        if (options.CanonicalNames) Step(BoneMatchKind.CanonicalName, sourceProfile.Canonical, targetProfile.Canonical, StringComparer.Ordinal);
        if (options.BuiltInTables) Step(BoneMatchKind.BuiltInTable, RigProfiles.StockCanonical, RigProfiles.StockCanonical, StringComparer.Ordinal);
        if (options.Fuzzy) FuzzyStep(sourceNames, sourceProfile, targetNames, targetProfile, map, kinds, options.FuzzyThreshold);

        return BoneMap.Create(
            [.. Enumerable.Range(0, ns).Select(i => new BoneMapBone(sourceNames[i], sourceParents[i]))],
            [.. Enumerable.Range(0, nt).Select(i => new BoneMapBone(targetNames[i], targetParents[i]))],
            map, kinds);
    }

    private static void FuzzyStep(
        IReadOnlyList<string> sourceNames, RigProfile sourceProfile, IReadOnlyList<string> targetNames, RigProfile targetProfile,
        int[] map, BoneMatchKind[] kinds, double threshold)
    {
        var used = new HashSet<int>(map.Where(s => s >= 0));
        var freeSources = Enumerable.Range(0, sourceNames.Count).Where(s => !used.Contains(s)).ToList();
        var freeTargets = Enumerable.Range(0, targetNames.Count).Where(t => map[t] < 0).ToList();
        if (freeSources.Count == 0 || freeTargets.Count == 0) return;

        var sTokens = freeSources.ToDictionary(s => s, s => BoneTokens.Parse(sourceProfile.Canonical(sourceNames[s])));
        var tTokens = freeTargets.ToDictionary(t => t, t => BoneTokens.Parse(targetProfile.Canonical(targetNames[t])));
        var score = new Dictionary<(int T, int S), double>();
        foreach (int t in freeTargets)
        {
            foreach (int s in freeSources) score[(t, s)] = BoneTokens.Similarity(tTokens[t], sTokens[s]);
        }

        // Greedy by descending score; a pair is taken only if it is unambiguous on both sides among
        // the bones still free at that point.
        var takenT = new HashSet<int>();
        var takenS = new HashSet<int>();
        foreach (var ((t, s), value) in score.Where(kv => kv.Value >= threshold).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.T).ThenBy(kv => kv.Key.S).ToList())
        {
            if (takenT.Contains(t) || takenS.Contains(s)) continue;
            bool targetTie = freeSources.Any(o => o != s && !takenS.Contains(o) && score[(t, o)] >= value);
            bool sourceTie = freeTargets.Any(o => o != t && !takenT.Contains(o) && score[(o, s)] >= value);
            if (targetTie || sourceTie) continue;
            map[t] = s;
            kinds[t] = BoneMatchKind.Fuzzy;
            takenT.Add(t);
            takenS.Add(s);
        }
    }
}
