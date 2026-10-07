using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using System.Text.RegularExpressions;
using Cairn.Rfa.Animation;

namespace Cairn.Rfa.Retarget;

/// <summary>
/// A two-bone IK chain (upper limb, lower limb, end effector), by canonical bone names.
/// </summary>
/// <param name="Upper">The bone at the shoulder or hip (its joint stays where FK puts it).</param>
/// <param name="Lower">The bone at the elbow or knee.</param>
/// <param name="End">The hand or foot: its joint is pinned to the source's, its model-space rotation kept.</param>
/// <param name="PoleBias">
/// Model-space metres added to the bend direction as the source limb straightens (arms: 0.15 m
/// down), so a longer target limb does not fold sideways. Read as the decimal it prints as.
/// </param>
public sealed record IkChain(string Upper, string Lower, string End, Vector3 PoleBias)
{
    /// <summary>A name for messages: the upper bone's canonical name.</summary>
    public override string ToString() => Upper;
}

/// <summary>One bone of a profile's expected bone list, by canonical names.</summary>
/// <param name="Name">Canonical bone name.</param>
/// <param name="Parent">Canonical parent name, or null for the root.</param>
public sealed record ProfileBone(string Name, string? Parent);

/// <summary>
/// What the retargeter needs to know about one rig, beyond its skeleton: how bone names become
/// canonical names (so different rigs line up), which child a branching bone "points at", the IK
/// chains, the root and pelvis, and where its T-pose rest mesh and stand clip come from. Built-ins
/// for the four stock humanoid rigs live in <see cref="RigProfiles"/>; <see cref="Generic(Skeleton)"/> makes
/// one for any skeleton. Saved and loaded as JSON (<see cref="ToJson"/>, <see cref="FromJson"/>).
/// </summary>
public sealed record RigProfile
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);

    /// <summary>Short name, used in output names (<c>af_{rig}_{clip}.rfa</c>), e.g. <c>female</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>A longer description for lists and reports.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Regular expressions removed from the lower-cased bone name, in order (for example the
    /// exporter's <c>^(ult2|park|...)-bdbn-</c> character prefix).
    /// </summary>
    public ImmutableArray<string> PrefixPatterns { get; init; } = [];

    /// <summary>Whole-name renames applied after the prefixes are removed (lower-cased name -> canonical).</summary>
    public ImmutableDictionary<string, string> Renames { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Canonical bone -> the canonical child it points at: the limb segment rest alignment swings
    /// and the report measures. Branching bones need one (spine04 -> head); root and pelvis are left out.
    /// </summary>
    public ImmutableDictionary<string, string> PrimaryChildren { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>Two-bone IK chains run after the rotation transfer (empty for non-humanoid rigs).</summary>
    public ImmutableArray<IkChain> IkChains { get; init; } = [];

    /// <summary>Metres: the pole bias fades in as the source elbow/knee comes closer than this to the shoulder-hand line.</summary>
    public double IkPoleFade { get; init; } = 0.10;

    /// <summary>Ticks: IK tracks are resampled with keys at most this far apart.</summary>
    public int IkMaxKeyGap { get; init; } = 320;

    /// <summary>Canonical name of the root bone (the parentless bone the clip's root motion drives).</summary>
    public string RootBone { get; init; } = "root";

    /// <summary>Canonical name of the pelvis (anchored by <see cref="RootMode.AnchorPelvis"/>), or null when the rig has none.</summary>
    public string? PelvisBone { get; init; } = "pelvis";

    /// <summary>File name of the mesh whose bind is a T-pose (the rest the retarget measures against).</summary>
    public string? RestMesh { get; init; }

    /// <summary>File name of the rig's stand clip (bone lengths and the pose of extra bones).</summary>
    public string? ReferenceClip { get; init; }

    /// <summary>The game AnimType that plays clips on this rig, when known.</summary>
    public string? AnimType { get; init; }

    /// <summary>Meshes sharing this rig's bone list (any clip for one plays on all).</summary>
    public ImmutableArray<string> MeshNames { get; init; } = [];

    /// <summary>
    /// The expected bone list in index order (canonical names and parents); used by
    /// <see cref="RigProfiles.FindFor(Skeleton)"/>. Empty when the profile fits any bone list.
    /// </summary>
    public ImmutableArray<ProfileBone> Bones { get; init; } = [];

    /// <summary>
    /// The canonical form of a bone name: lower-cased, every <see cref="PrefixPatterns"/> match
    /// removed, then looked up in <see cref="Renames"/>.
    /// </summary>
    /// <exception cref="FormatException">A prefix pattern is not a valid regular expression.</exception>
    public string Canonical(string boneName)
    {
        ArgumentNullException.ThrowIfNull(boneName);
        string n = boneName.ToLowerInvariant();
        foreach (string pattern in PrefixPatterns.IsDefault ? [] : PrefixPatterns) n = GetRegex(pattern).Replace(n, string.Empty);
        return !Renames.IsEmpty && Renames.TryGetValue(n, out var renamed) ? renamed : n;
    }

    /// <summary>The canonical names of every bone of a skeleton, in index order.</summary>
    public ImmutableArray<string> CanonicalNames(IReadOnlyList<string> boneNames)
    {
        ArgumentNullException.ThrowIfNull(boneNames);
        var result = new string[boneNames.Count];
        for (int i = 0; i < result.Length; i++) result[i] = Canonical(boneNames[i]);
        return [.. result];
    }

    /// <summary>The index of the first bone whose canonical name is <paramref name="canonical"/>, or -1.</summary>
    public int IndexOf(IReadOnlyList<string> boneNames, string? canonical)
    {
        ArgumentNullException.ThrowIfNull(boneNames);
        if (string.IsNullOrEmpty(canonical)) return -1;
        for (int i = 0; i < boneNames.Count; i++)
        {
            if (Canonical(boneNames[i]) == canonical) return i;
        }
        return -1;
    }

    /// <summary>
    /// Problems that would make the profile misbehave, in plain words (empty when it is fine): bad
    /// regular expressions, IK chains naming the same bone twice, non-positive IK settings.
    /// </summary>
    public ImmutableArray<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) problems.Add("The profile has no name. Give it a short name such as 'female'; it is used in output file names.");
        foreach (string pattern in PrefixPatterns.IsDefault ? [] : PrefixPatterns)
        {
            try { _ = new Regex(pattern, RegexOptions.CultureInvariant); }
            catch (ArgumentException ex)
            {
                problems.Add($"The prefix pattern '{pattern}' is not a valid regular expression ({ex.Message}). Fix it or remove it.");
            }
        }
        foreach (var chain in IkChains.IsDefault ? [] : IkChains)
        {
            if (chain.Upper == chain.Lower || chain.Lower == chain.End || chain.Upper == chain.End)
                problems.Add($"The IK chain '{chain.Upper}' names the same bone twice. A chain needs three different bones: upper, lower and end.");
        }
        if (IkMaxKeyGap <= 0) problems.Add($"The IK key gap is {IkMaxKeyGap} ticks; it must be above 0 (320 is one key every 1/15 s).");
        if (IkPoleFade < 0) problems.Add($"The IK pole fade is {IkPoleFade} m; it must be 0 or more (0.10 is the default).");
        return [.. problems];
    }

    /// <summary>
    /// True for a leg chain: its end bone is a foot (or its lower bone a lower leg / calf / shin).
    /// Every other chain counts as an arm for <see cref="RetargetOptions.IkArms"/> / <see cref="RetargetOptions.IkLegs"/>.
    /// </summary>
    public static bool IsLegChain(IkChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var end = BoneTokens.Parse(chain.End).Tokens;
        var lower = BoneTokens.Parse(chain.Lower).Tokens;
        return end.Contains("foot") || end.Contains("ankle") || (lower.Contains("leg") && !lower.Contains("arm"));
    }

    /// <summary>The profile as indented JSON.</summary>
    public string ToJson() => RetargetJson.Write(this);

    /// <summary>Reads a profile saved by <see cref="ToJson"/>.</summary>
    /// <exception cref="FormatException">The text is not a rig profile.</exception>
    public static RigProfile FromJson(string json)
    {
        var profile = RetargetJson.Read<RigProfile>(json, "rig profile");
        // Missing arrays deserialise as their initialisers; explicit nulls do not, so repair them.
        var repaired = profile with
        {
            PrefixPatterns = profile.PrefixPatterns.IsDefault ? [] : profile.PrefixPatterns,
            Renames = profile.Renames ?? ImmutableDictionary<string, string>.Empty,
            PrimaryChildren = profile.PrimaryChildren ?? ImmutableDictionary<string, string>.Empty,
            IkChains = profile.IkChains.IsDefault ? [] : profile.IkChains,
            MeshNames = profile.MeshNames.IsDefault ? [] : profile.MeshNames,
            Bones = profile.Bones.IsDefault ? [] : profile.Bones,
            Name = profile.Name ?? string.Empty,
            Title = profile.Title ?? string.Empty,
            RootBone = profile.RootBone ?? "root",
        };
        // A null INSIDE a list or map cannot be repaired the same way; it would surface later as a crash
        // in Canonical, Validate or the rig matcher.
        if (repaired.PrefixPatterns.Any(p => p is null) || repaired.MeshNames.Any(m => m is null)
            || repaired.IkChains.Any(c => c?.Upper is null || c.Lower is null || c.End is null)
            || repaired.Bones.Any(b => b?.Name is null)
            || repaired.Renames.Values.Any(v => v is null) || repaired.PrimaryChildren.Values.Any(v => v is null))
        {
            throw new FormatException("This rig profile has an empty entry (null) in one of its lists: a prefix pattern, mesh name, IK chain bone, "
                + "bone name, rename or primary child. Remove it or fill it in, or save the profile again from Cairn.");
        }
        return repaired;
    }

    /// <summary>
    /// A profile generated for any skeleton:
    /// <list type="bullet">
    /// <item>canonical names drop the exporter's <c>xxxx-bdbn-</c> prefix (and use the stock civilian
    /// rename table when the skeleton has those names);</item>
    /// <item>root = the parentless bone (the one with most descendants if there are several);</item>
    /// <item>pelvis = the bone named pelvis / hips / hip, or none;</item>
    /// <item>primary child = for IK bones the next bone of the chain, else the only child, else a
    /// spine/neck/head child, else the child with most descendants (thumbs last);</item>
    /// <item>IK chains only when humanoid names are found: upper arm / lower arm (forearm) / hand and
    /// upper leg (thigh) / lower leg (calf, shin) / foot on each side, each a real parent chain.</item>
    /// </list>
    /// </summary>
    public static RigProfile Generic(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        return Generic(skeleton.Names, skeleton.EffectiveParents);
    }

    /// <summary>A generated profile for a bone list (see <see cref="Generic(Skeleton)"/>).</summary>
    public static RigProfile Generic(IReadOnlyList<string> boneNames, IReadOnlyList<int> parents)
    {
        ArgumentNullException.ThrowIfNull(boneNames);
        ArgumentNullException.ThrowIfNull(parents);
        int n = boneNames.Count;
        if (parents.Count != n) throw new ArgumentException("Every bone needs a parent index.", nameof(parents));
        var effective = ForwardKinematics.EffectiveParents([.. parents]);

        var profile = new RigProfile
        {
            Name = "generic",
            Title = $"generated profile ({n} bones)",
            PrefixPatterns = [RigProfiles.GenericPrefixPattern],
            IkPoleFade = 0.10,
            IkMaxKeyGap = 320,
        };
        var stripped = profile.CanonicalNames(boneNames);
        if (stripped.Any(RigProfiles.CivilianRenames.ContainsKey)) profile = profile with { Renames = RigProfiles.CivilianRenames };
        var names = profile.CanonicalNames(boneNames);

        var children = new List<int>[n];
        for (int i = 0; i < n; i++) children[i] = [];
        for (int i = 0; i < n; i++)
        {
            if (effective[i] >= 0) children[effective[i]].Add(i);
        }
        var descendants = new int[n];
        for (int i = 0; i < n; i++)
        {
            for (int p = effective[i], guard = 0; p >= 0 && guard <= n; p = effective[p], guard++) descendants[p]++;
        }

        int root = -1;
        for (int i = 0; i < n; i++)
        {
            if (effective[i] < 0 && (root < 0 || descendants[i] > descendants[root])) root = i;
        }

        int pelvis = -1;
        foreach (string want in new[] { "pelvis", "hips", "hip" })
        {
            pelvis = names.IndexOf(want);
            if (pelvis >= 0) break;
        }
        if (pelvis < 0)
        {
            for (int i = 0; i < n && pelvis < 0; i++)
            {
                var t = BoneTokens.Parse(names[i]);
                if (t.Side == BoneSide.None && t.Tokens.Contains("pelvis")) pelvis = i;
            }
        }

        var chains = new List<IkChain>();
        var chainNext = new Dictionary<int, int>();
        foreach (var (upperWords, lowerWords, endWords, bias, endExclude) in HumanoidLimbs)
        {
            foreach (var side in new[] { BoneSide.Left, BoneSide.Right })
            {
                for (int e = 0; e < n; e++)
                {
                    int l = effective[e];
                    int u = l >= 0 ? effective[l] : -1;
                    if (u < 0) continue;
                    if (!Is(names[e], side, endWords, endExclude) || !Is(names[l], side, lowerWords, []) || !Is(names[u], side, upperWords, [])) continue;
                    if (chainNext.ContainsKey(u) || chainNext.ContainsKey(l)) continue;
                    chains.Add(new IkChain(names[u], names[l], names[e], bias));
                    chainNext[u] = l;
                    chainNext[l] = e;
                    break;
                }
            }
        }

        var primary = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            if (i == root || i == pelvis || children[i].Count == 0 || primary.ContainsKey(names[i])) continue;
            int pick;
            if (chainNext.TryGetValue(i, out int next)) pick = next;
            else if (children[i].Count == 1) pick = children[i][0];
            else
            {
                pick = children[i].FirstOrDefault(c => BoneTokens.Parse(names[c]).Tokens.Any(t => t is "spine" or "neck" or "head" or "chest"), -1);
                if (pick < 0)
                {
                    pick = children[i]
                        .OrderByDescending(c => descendants[c])
                        .ThenBy(c => BoneTokens.Parse(names[c]).Tokens.Contains("thumb") ? 1 : 0)
                        .ThenBy(c => c)
                        .First();
                }
            }
            primary[names[i]] = names[pick];
        }

        return profile with
        {
            RootBone = root >= 0 ? names[root] : "root",
            PelvisBone = pelvis >= 0 ? names[pelvis] : null,
            IkChains = [.. chains],
            PrimaryChildren = primary.ToImmutable(),
            Bones = [.. Enumerable.Range(0, n).Select(i => new ProfileBone(names[i], effective[i] >= 0 ? names[effective[i]] : null))],
        };

        static bool Is(string name, BoneSide side, string[] words, string[] exclude)
        {
            var t = BoneTokens.Parse(name);
            if (t.Side != side) return false;
            foreach (string w in words)
            {
                if (!t.Tokens.Contains(w)) return false;
            }
            foreach (string x in exclude)
            {
                if (t.Tokens.Contains(x)) return false;
            }
            // "upper arm" must not also be "lower" (and the other way round).
            if (words.Contains("upper") && t.Tokens.Contains("lower")) return false;
            if (words.Contains("lower") && t.Tokens.Contains("upper")) return false;
            return true;
        }
    }

    private static readonly (string[] Upper, string[] Lower, string[] End, Vector3 Bias, string[] EndExclude)[] HumanoidLimbs =
    [
        (["upper", "arm"], ["lower", "arm"], ["hand"], new Vector3(0f, -0.15f, 0f), ["finger", "thumb"]),
        (["upper", "leg"], ["lower", "leg"], ["foot"], Vector3.Zero, ["toe"]),
    ];

    private static Regex GetRegex(string pattern)
    {
        return RegexCache.GetOrAdd(pattern, p =>
        {
            try
            {
                return new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new FormatException($"The prefix pattern '{p}' is not a valid regular expression ({ex.Message}). Fix it in the rig profile.", ex);
            }
        });
    }
}
