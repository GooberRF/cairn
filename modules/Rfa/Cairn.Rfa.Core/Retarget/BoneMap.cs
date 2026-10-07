using System.Collections.Immutable;

namespace Cairn.Rfa.Retarget;

/// <summary>How a target bone is driven, given its source and its parent's source.</summary>
public enum BoneMapStatus
{
    /// <summary>
    /// Has a source bone, and its target parent maps to that source bone's parent (or both are
    /// roots): the keys transfer one for one, keeping times and eases.
    /// </summary>
    Mapped,

    /// <summary>
    /// Has a source bone, but its target parent maps to a source ANCESTOR other than the source
    /// bone's parent (the merc's spine03 hangs off root, rig A's off pelvis): the composed source
    /// chain is resampled at the union of its key times; eases survive only where keys coincide.
    /// </summary>
    Reparented,

    /// <summary>
    /// Has a source bone, but its target parent maps to a source bone that is not an ancestor of it
    /// at all: the rotation is evaluated in model space at every key time of both source chains
    /// (eases are lost). Supported, but rarely what was meant.
    /// </summary>
    CrossBranch,

    /// <summary>No source bone: a static extra bone holding the reference clip's (or bind) pose, riding on its parent.</summary>
    Unmapped,

    /// <summary>Has a source bone, but its target parent has none. Unsupported: map the parent or unmap this bone.</summary>
    ParentUnmapped,

    /// <summary>The target root maps to a source bone that is not a root. Unsupported: map the root to the source root.</summary>
    RootFromNonRoot,
}

/// <summary>Which step of <see cref="BoneMapper"/> produced a pairing.</summary>
public enum BoneMatchKind
{
    /// <summary>Not mapped.</summary>
    None,
    /// <summary>Identical names (case-insensitive).</summary>
    ExactName,
    /// <summary>Identical canonical names (each side through its own profile).</summary>
    CanonicalName,
    /// <summary>Identical names under the built-in stock rules (prefixes and the civilian table).</summary>
    BuiltInTable,
    /// <summary>Similar body words (see <see cref="BoneMapper"/>).</summary>
    Fuzzy,
    /// <summary>Set by hand (<see cref="BoneMap.With(int, int)"/>).</summary>
    Manual,
}

/// <summary>Severity of a bone map problem.</summary>
public enum BoneMapSeverity
{
    /// <summary>The retarget will run, but check the result.</summary>
    Warning,
    /// <summary>The retarget cannot run until this is fixed.</summary>
    Error,
}

/// <summary>A bone as a bone map records it.</summary>
/// <param name="Name">The bone's name as stored in the mesh.</param>
/// <param name="Parent">Parent index, -1 for a root.</param>
public sealed record BoneMapBone(string Name, int Parent);

/// <summary>A bone reference: index and name.</summary>
/// <param name="Index">Bone index in its skeleton.</param>
/// <param name="Name">The bone's name.</param>
public sealed record BoneRef(int Index, string Name);

/// <summary>One target bone's row of a bone map.</summary>
/// <param name="TargetIndex">Index in the target skeleton.</param>
/// <param name="TargetName">Name in the target skeleton.</param>
/// <param name="SourceIndex">Index of the source bone that drives it, or -1.</param>
/// <param name="SourceName">Name of that source bone, or null.</param>
/// <param name="Status">How the bone will be driven (recomputed whenever the map changes).</param>
/// <param name="Match">Which mapping step produced the pairing.</param>
public sealed record BoneMapEntry(int TargetIndex, string TargetName, int SourceIndex, string? SourceName, BoneMapStatus Status, BoneMatchKind Match)
{
    /// <summary>True when the bone has a source.</summary>
    public bool IsMapped => SourceIndex >= 0;
}

/// <summary>A problem with a bone map, in plain words.</summary>
/// <param name="Severity">Error (the retarget refuses) or warning.</param>
/// <param name="TargetIndex">The target bone concerned, or -1.</param>
/// <param name="Message">What is wrong and how to fix it.</param>
public sealed record BoneMapProblem(BoneMapSeverity Severity, int TargetIndex, string Message);

/// <summary>
/// Which source bone drives each target bone. Immutable; <see cref="With(int, int)"/> returns a
/// changed copy with every status recomputed. Saved and loaded as JSON.
/// </summary>
public sealed record BoneMap
{
    /// <summary>The source skeleton's bones (names and parents).</summary>
    public ImmutableArray<BoneMapBone> SourceBones { get; init; } = [];

    /// <summary>The target skeleton's bones (names and parents).</summary>
    public ImmutableArray<BoneMapBone> TargetBones { get; init; } = [];

    /// <summary>One row per target bone, in target index order.</summary>
    public ImmutableArray<BoneMapEntry> Entries { get; init; } = [];

    /// <summary>Source bones no target bone follows (their motion is dropped).</summary>
    public ImmutableArray<BoneRef> UnusedSourceBones { get; init; } = [];

    /// <summary>The source bone of a target bone, or -1.</summary>
    public int SourceOf(int targetIndex) => (uint)targetIndex < (uint)Entries.Length ? Entries[targetIndex].SourceIndex : -1;

    /// <summary>The source index of every target bone (-1 = none), in target order.</summary>
    public int[] ToArray() => [.. Entries.Select(e => e.SourceIndex)];

    /// <summary>Number of target bones with each status.</summary>
    public int Count(BoneMapStatus status) => Entries.Count(e => e.Status == status);

    /// <summary>
    /// Builds a map from target-to-source indices, computing statuses and unused source bones.
    /// </summary>
    /// <param name="sourceBones">Source bone names and parents.</param>
    /// <param name="targetBones">Target bone names and parents.</param>
    /// <param name="targetToSource">For each target bone, its source index or -1.</param>
    /// <param name="matches">How each pairing was found (optional; Manual for mapped bones when omitted).</param>
    public static BoneMap Create(
        IReadOnlyList<BoneMapBone> sourceBones, IReadOnlyList<BoneMapBone> targetBones,
        IReadOnlyList<int> targetToSource, IReadOnlyList<BoneMatchKind>? matches = null)
    {
        ArgumentNullException.ThrowIfNull(sourceBones);
        ArgumentNullException.ThrowIfNull(targetBones);
        ArgumentNullException.ThrowIfNull(targetToSource);
        if (targetToSource.Count != targetBones.Count)
            throw new ArgumentException("There must be one source index per target bone.", nameof(targetToSource));
        int ns = sourceBones.Count, nt = targetBones.Count;
        var map = new int[nt];
        for (int i = 0; i < nt; i++)
        {
            int s = targetToSource[i];
            map[i] = s >= 0 && s < ns ? s : -1;
        }
        var sParents = Sanitise(sourceBones.Select(b => b.Parent).ToArray());
        var tParents = Sanitise(targetBones.Select(b => b.Parent).ToArray());

        var entries = new BoneMapEntry[nt];
        for (int i = 0; i < nt; i++)
        {
            int s = map[i];
            BoneMapStatus status;
            if (s < 0) status = BoneMapStatus.Unmapped;
            else if (tParents[i] < 0) status = sParents[s] < 0 ? BoneMapStatus.Mapped : BoneMapStatus.RootFromNonRoot;
            else
            {
                int sp = map[tParents[i]];
                if (sp < 0) status = BoneMapStatus.ParentUnmapped;
                else if (sParents[s] == sp) status = BoneMapStatus.Mapped;
                else if (IsAncestor(sParents, sp, s)) status = BoneMapStatus.Reparented;
                else status = BoneMapStatus.CrossBranch;
            }
            var kind = s < 0 ? BoneMatchKind.None : matches is not null && i < matches.Count && matches[i] != BoneMatchKind.None ? matches[i] : BoneMatchKind.Manual;
            entries[i] = new BoneMapEntry(i, targetBones[i].Name, s, s >= 0 ? sourceBones[s].Name : null, status, kind);
        }
        var used = new bool[ns];
        foreach (int s in map)
        {
            if (s >= 0) used[s] = true;
        }
        var unused = Enumerable.Range(0, ns).Where(s => !used[s]).Select(s => new BoneRef(s, sourceBones[s].Name));
        return new BoneMap { SourceBones = [.. sourceBones], TargetBones = [.. targetBones], Entries = [.. entries], UnusedSourceBones = [.. unused] };
    }

    /// <summary>A copy with <paramref name="targetIndex"/> driven by <paramref name="sourceIndex"/> (-1 to unmap); statuses are recomputed.</summary>
    public BoneMap With(int targetIndex, int sourceIndex)
    {
        if ((uint)targetIndex >= (uint)Entries.Length) throw new ArgumentOutOfRangeException(nameof(targetIndex));
        if (sourceIndex < -1 || sourceIndex >= SourceBones.Length) throw new ArgumentOutOfRangeException(nameof(sourceIndex));
        var map = ToArray();
        map[targetIndex] = sourceIndex;
        var kinds = Entries.Select(e => e.Match).ToArray();
        kinds[targetIndex] = sourceIndex < 0 ? BoneMatchKind.None : BoneMatchKind.Manual;
        return Create(SourceBones, TargetBones, map, kinds);
    }

    /// <summary>A copy with the named target bone driven by the named source bone (null to unmap). Names compare case-insensitively.</summary>
    /// <exception cref="ArgumentException">A name is not in its skeleton.</exception>
    public BoneMap With(string targetName, string? sourceName)
    {
        ArgumentNullException.ThrowIfNull(targetName);
        int t = IndexOf(TargetBones, targetName);
        if (t < 0) throw new ArgumentException($"The target skeleton has no bone named '{targetName}'.", nameof(targetName));
        int s = -1;
        if (sourceName is not null)
        {
            s = IndexOf(SourceBones, sourceName);
            if (s < 0) throw new ArgumentException($"The source skeleton has no bone named '{sourceName}'.", nameof(sourceName));
        }
        return With(t, s);
    }

    /// <summary>
    /// What would stop or spoil a retarget with this map, in plain words: errors for unsupported
    /// cases (a mapped bone whose parent has no source; the target root following a non-root bone),
    /// warnings for two target bones following one source bone and for reparented / cross-branch
    /// bones (resampled, eases partly lost).
    /// </summary>
    public ImmutableArray<BoneMapProblem> Validate()
    {
        var problems = new List<BoneMapProblem>();
        foreach (var e in Entries)
        {
            switch (e.Status)
            {
                case BoneMapStatus.ParentUnmapped:
                {
                    string parent = TargetBones[TargetBones[e.TargetIndex].Parent].Name;
                    problems.Add(new BoneMapProblem(BoneMapSeverity.Error, e.TargetIndex,
                        $"The target bone '{e.TargetName}' follows the source bone '{e.SourceName}', but its parent '{parent}' has no source bone. "
                        + $"A bone can only follow the source when its parent does too. Map '{parent}' to a source bone, or unmap '{e.TargetName}' so it holds a still pose."));
                    break;
                }
                case BoneMapStatus.RootFromNonRoot:
                    problems.Add(new BoneMapProblem(BoneMapSeverity.Error, e.TargetIndex,
                        $"The target root '{e.TargetName}' follows '{e.SourceName}', which is not the source's root. "
                        + "Root motion is in model space, so the root must follow the source root. Map it to the source's root bone."));
                    break;
                case BoneMapStatus.Reparented:
                    problems.Add(new BoneMapProblem(BoneMapSeverity.Warning, e.TargetIndex,
                        $"'{e.TargetName}' hangs off a different parent than '{e.SourceName}' does in the source; its track is resampled from the source chain, "
                        + "so its eases are kept only where keys coincide. This is expected for the merc's spine03."));
                    break;
                case BoneMapStatus.CrossBranch:
                    problems.Add(new BoneMapProblem(BoneMapSeverity.Warning, e.TargetIndex,
                        $"'{e.TargetName}' follows '{e.SourceName}', but its parent follows a source bone on another branch of the skeleton. "
                        + "It will be evaluated in model space (eases lost). Check that this pairing is what you meant."));
                    break;
            }
        }
        foreach (var group in Entries.Where(e => e.SourceIndex >= 0).GroupBy(e => e.SourceIndex).Where(g => g.Count() > 1))
        {
            var names = string.Join("', '", group.Select(e => e.TargetName));
            problems.Add(new BoneMapProblem(BoneMapSeverity.Warning, group.First().TargetIndex,
                $"The target bones '{names}' all follow the source bone '{group.First().SourceName}'. "
                + "They will move identically; unmap the ones that should hold still, or map them to their own source bones."));
        }
        return [.. problems];
    }

    /// <summary>The map as indented JSON.</summary>
    public string ToJson() => RetargetJson.Write(this);

    /// <summary>
    /// Reads a map saved by <see cref="ToJson"/>; statuses and unused bones are recomputed from the
    /// bone lists and the source indices, so a hand-edited file cannot carry stale statuses.
    /// </summary>
    /// <exception cref="FormatException">The text is not a bone map.</exception>
    public static BoneMap FromJson(string json)
    {
        var raw = RetargetJson.Read<BoneMap>(json, "bone map");
        if (raw.SourceBones.IsDefault || raw.TargetBones.IsDefault || raw.Entries.IsDefault)
            throw new FormatException("This bone map file is missing its bone lists or entries. Save it again from Cairn.");
        if (raw.SourceBones.Any(b => b?.Name is null) || raw.TargetBones.Any(b => b?.Name is null) || raw.Entries.Any(e => e is null))
            throw new FormatException("This bone map file has an empty bone or entry (a bone without a name, or null). Save it again from Cairn.");
        if (raw.Entries.Length != raw.TargetBones.Length)
            throw new FormatException($"This bone map has {raw.Entries.Length} entries for {raw.TargetBones.Length} target bones. Save it again from Cairn.");
        foreach (var e in raw.Entries)
        {
            if (e.SourceIndex >= raw.SourceBones.Length || e.SourceIndex < -1)
                throw new FormatException($"The bone map entry for '{e.TargetName}' names source bone {e.SourceIndex}, but the source has {raw.SourceBones.Length} bones.");
        }
        return Create(raw.SourceBones, raw.TargetBones, [.. raw.Entries.Select(e => e.SourceIndex)], [.. raw.Entries.Select(e => e.Match)]);
    }

    /// <summary>True when this map was made for skeletons with these bone names (same count and names, case-insensitive).</summary>
    public bool Fits(IReadOnlyList<string> sourceNames, IReadOnlyList<string> targetNames)
    {
        ArgumentNullException.ThrowIfNull(sourceNames);
        ArgumentNullException.ThrowIfNull(targetNames);
        if (sourceNames.Count != SourceBones.Length || targetNames.Count != TargetBones.Length || Entries.Length != TargetBones.Length) return false;
        for (int i = 0; i < sourceNames.Count; i++)
        {
            if (!string.Equals(sourceNames[i], SourceBones[i].Name, StringComparison.OrdinalIgnoreCase)) return false;
        }
        for (int i = 0; i < targetNames.Count; i++)
        {
            if (!string.Equals(targetNames[i], TargetBones[i].Name, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static int IndexOf(ImmutableArray<BoneMapBone> bones, string name)
    {
        for (int i = 0; i < bones.Length; i++)
        {
            if (string.Equals(bones[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static int[] Sanitise(int[] parents) => Animation.ForwardKinematics.EffectiveParents(parents);

    private static bool IsAncestor(int[] parents, int ancestor, int bone)
    {
        int guard = 0;
        for (int p = parents[bone]; p >= 0 && guard <= parents.Length; p = parents[p], guard++)
        {
            if (p == ancestor) return true;
        }
        return false;
    }
}
