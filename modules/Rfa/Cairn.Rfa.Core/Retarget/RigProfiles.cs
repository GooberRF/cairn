using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;

namespace Cairn.Rfa.Retarget;

/// <summary>
/// The built-in profiles of the four stock humanoid rigs, with exactly the data of the reference
/// (<c>research/anim_retarget/tools/rigs.py</c>: RIGS, PRIMARY_CHILD, IK_CHAINS, IK_POLE_FADE,
/// IK_MAX_KEY_GAP, the prefix pattern and the civilian rename table) and the bone lists of
/// <c>rigs.md</c>. All four canonicalise names the same way (rigs.py has one <c>canonical</c>), so
/// their canonical names line up and any two of them map onto each other by name.
/// </summary>
public static class RigProfiles
{
    /// <summary>The stock exporter prefixes (rigs.py <c>_PREFIX</c>), applied to the lower-cased name.</summary>
    public const string StockPrefixPattern = "^(ult2|park|engd|esgd|rtgd|mnrm|mrc1|mcom)-bdbn-";

    /// <summary>Any exporter character prefix <c>xxxx-bdbn-</c> (what <see cref="SkeletonMatcher"/> drops).</summary>
    public const string GenericPrefixPattern = "^[a-z0-9]+-bdbn-";

    /// <summary>The civilian (<c>tech- ...</c>) rename table of rigs.py.</summary>
    public static ImmutableDictionary<string, string> CivilianRenames { get; } = new Dictionary<string, string>
    {
        ["tech- root"] = "root", ["tech- pelvis"] = "pelvis",
        ["tech- 1spine"] = "spine03", ["tech- 1spine01"] = "spine04", ["tech- 1head"] = "head",
        ["tech- arm-l-upper"] = "upperarm-l", ["tech- arm-l-lower"] = "lowerarm-l", ["tech- arm-l-hand"] = "hand-l",
        ["tech- arm-l-fingers"] = "fingers-l", ["tech- arm-l-fingers01"] = "fingers-l2",
        ["tech- arm-l-thumb"] = "thumb-l", ["tech- arm-l-thumb01"] = "thumb-l2",
        ["tech- arm-r-upper"] = "upperarm-r", ["tech- arm-r-lower"] = "lowerarm-r", ["tech- arm-r-hand"] = "hand-r",
        ["tech- arm-r-fingers"] = "fingers-r", ["tech- arm-r-fingers01"] = "fingers-r2",
        ["tech- arm-r-thumb"] = "thumb-r", ["tech- arm-r-thumb01"] = "thumb-r2",
        ["tech- leg-l-upper"] = "upperleg-l", ["tech- leg-l-lower"] = "lowerleg-l", ["tech- foot-l"] = "foot-l",
        ["tech- foot-l-toes"] = "toes-l",
        ["tech- leg-r-upper"] = "upperleg-r", ["tech- leg-r-lower"] = "lowerleg-r", ["tech- foot-r"] = "foot-r",
        ["tech- foot-r-toes"] = "toes-r",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>rigs.py PRIMARY_CHILD.</summary>
    public static ImmutableDictionary<string, string> StockPrimaryChildren { get; } = new Dictionary<string, string>
    {
        ["spine03"] = "spine04", ["spine04"] = "head",
        ["upperarm-l"] = "lowerarm-l", ["lowerarm-l"] = "hand-l", ["hand-l"] = "fingers-l", ["fingers-l"] = "fingers-l2",
        ["upperarm-r"] = "lowerarm-r", ["lowerarm-r"] = "hand-r", ["hand-r"] = "fingers-r", ["fingers-r"] = "fingers-r2",
        ["upperleg-l"] = "lowerleg-l", ["lowerleg-l"] = "foot-l", ["foot-l"] = "toes-l",
        ["upperleg-r"] = "lowerleg-r", ["lowerleg-r"] = "foot-r", ["foot-r"] = "toes-r",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>rigs.py IK_CHAINS (arms biased 0.15 m down, legs unbiased), in the reference's order.</summary>
    public static ImmutableArray<IkChain> StockIkChains { get; } =
    [
        new("upperarm-l", "lowerarm-l", "hand-l", new Vector3(0f, -0.15f, 0f)),
        new("upperarm-r", "lowerarm-r", "hand-r", new Vector3(0f, -0.15f, 0f)),
        new("upperleg-l", "lowerleg-l", "foot-l", Vector3.Zero),
        new("upperleg-r", "lowerleg-r", "foot-r", Vector3.Zero),
    ];

    private static readonly RigProfile StockBase = new()
    {
        PrefixPatterns = [StockPrefixPattern],
        Renames = CivilianRenames,
        PrimaryChildren = StockPrimaryChildren,
        IkChains = StockIkChains,
        IkPoleFade = 0.10,
        IkMaxKeyGap = 320,
        RootBone = "root",
        PelvisBone = "pelvis",
    };

    private const string RigABones =
        "fingers-l:hand-l fingers-l2:fingers-l fingers-r:hand-r fingers-r2:fingers-r foot-l:lowerleg-l foot-r:lowerleg-r "
        + "hand-l:lowerarm-l hand-r:lowerarm-r head:spine04 lowerarm-l:upperarm-l lowerarm-r:upperarm-r "
        + "lowerleg-l:upperleg-l lowerleg-r:upperleg-r pelvis:root root: spine03:pelvis spine04:spine03 thumb-l:hand-l "
        + "thumb-r:hand-r toes-l:foot-l toes-r:foot-r upperarm-l:spine04 upperarm-r:spine04 upperleg-l:pelvis upperleg-r:pelvis";

    private const string FemaleBones =
        "fingers-l:hand-l fingers-r:hand-r fingers-r2:fingers-r fingers-l2:fingers-l foot-l:lowerleg-l foot-r:lowerleg-r "
        + "hand-l:lowerarm-l hand-r:lowerarm-r head:spine04 lowerarm-l:upperarm-l lowerarm-r:upperarm-r "
        + "lowerleg-l:upperleg-l lowerleg-r:upperleg-r pelvis:root root: spine03:pelvis spine04:spine03 thumb-l:hand-l "
        + "thumb-r:hand-r toes-l:foot-l toes-r:foot-r upperarm-l:spine04 upperarm-r:spine04 upperleg-l:pelvis upperleg-r:pelvis";

    private const string MercBones =
        "fingers-l:hand-l fingers-l2:fingers-l fingers-r:hand-r fingers-r2:fingers-r foot-l:lowerleg-l foot-r:lowerleg-r "
        + "hand-l:lowerarm-l hand-r:lowerarm-r head:spine04 lowerarm-l:upperarm-l lowerarm-r:upperarm-r "
        + "lowerleg-l:upperleg-l lowerleg-r:upperleg-r pelvis:root root: shoulderpad-l:spine04 shoulderpad-r:spine04 "
        + "spine03:root spine04:spine03 thumb-l:hand-l thumb-r:hand-r toes-l:foot-l toes-r:foot-r "
        + "upperarm-l:spine04 upperarm-r:spine04 upperleg-l:pelvis upperleg-r:pelvis";

    private const string CivilianBones =
        "head:spine04 spine03:pelvis spine04:spine03 fingers-l:hand-l fingers-l2:fingers-l hand-l:lowerarm-l "
        + "lowerarm-l:upperarm-l thumb-l:hand-l thumb-l2:thumb-l upperarm-l:spine04 fingers-r:hand-r fingers-r2:fingers-r "
        + "hand-r:lowerarm-r lowerarm-r:upperarm-r thumb-r:hand-r thumb-r2:thumb-r upperarm-r:spine04 foot-l:lowerleg-l "
        + "toes-l:foot-l foot-r:lowerleg-r toes-r:foot-r lowerleg-l:upperleg-l upperleg-l:pelvis lowerleg-r:upperleg-r "
        + "upperleg-r:pelvis pelvis:root root:";

    /// <summary>Rig A: the miner1 AnimType (ult2-/park- names, 25 bones). Rest ult2_guard.v3c, stand ult2_stand.rfa.</summary>
    public static RigProfile RigA { get; } = StockBase with
    {
        Name = "A",
        Title = "rig A: miner1 AnimType (ult2-/park- 25 bones)",
        RestMesh = "ult2_guard.v3c",
        ReferenceClip = "ult2_stand.rfa",
        AnimType = "miner1",
        MeshNames = ["miner.v3c", "ult2_guard.v3c", "parker_sci.v3c", "parker_suit.v3c", "multi_guard2.v3c",
            "Envirosuit_Guard.v3c", "elite_security_guard.v3c", "riot_guard.v3c", "non_env_miner_male.v3c"],
        Bones = ParseBones(RigABones),
    };

    /// <summary>Rig B: the multi_female AnimType (nurse1.vcm, 25 bones, indices 1-3 reordered). Stand mnr3f_12mm_stand.rfa.</summary>
    public static RigProfile Female { get; } = StockBase with
    {
        Name = "female",
        Title = "rig B: multi_female AnimType (nurse1.vcm, 25 bones, indices 1-3 reordered)",
        RestMesh = "nurse1.v3c",
        ReferenceClip = "mnr3f_12mm_stand.rfa",
        AnimType = "multi_female",
        MeshNames = ["nurse1.v3c", "non_env_miner_fem.v3c", "admin_fem.v3c", "masako.v3c", "eos.v3c"],
        Bones = ParseBones(FemaleBones),
    };

    /// <summary>
    /// Rig C: the multi_merc AnimType (merc_com.vcm, 27 bones, spine03 parented to root). Rest
    /// merc_grunt.v3c (merc_com's bind has the arms down), stand mrc2_stand_12mm.rfa.
    /// </summary>
    public static RigProfile Merc { get; } = StockBase with
    {
        Name = "merc",
        Title = "rig C: multi_merc AnimType (merc_com.vcm, 27 bones, spine03 parented to root)",
        RestMesh = "merc_grunt.v3c",
        ReferenceClip = "mrc2_stand_12mm.rfa",
        AnimType = "multi_merc",
        MeshNames = ["merc_grunt.v3c", "merc_com.v3c"],
        Bones = ParseBones(MercBones),
    };

    /// <summary>Rig D: the multi_civilian AnimType (tech01.vcm, 27 bones, own names, thumb01 per hand). Stand tech_12mm_stand.rfa.</summary>
    public static RigProfile Civilian { get; } = StockBase with
    {
        Name = "civilian",
        Title = "rig D: multi_civilian AnimType (tech01.vcm, 27 bones, own names, thumb01 per hand)",
        RestMesh = "tech01.v3c",
        ReferenceClip = "tech_12mm_stand.rfa",
        AnimType = "multi_civilian",
        MeshNames = ["tech01.v3c", "ult_scientist.v3c", "medic01.v3c", "env_scientist.v3c", "Hendrix.v3c",
            "admin_male.v3c", "admin_male2.v3c"],
        Bones = ParseBones(CivilianBones),
    };

    /// <summary>The four built-ins, rig A first.</summary>
    public static ImmutableArray<RigProfile> BuiltIn { get; } = [RigA, Female, Merc, Civilian];

    /// <summary>The built-in profile with this name (case-insensitive), or null.</summary>
    public static RigProfile? Get(string name) =>
        BuiltIn.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The built-in profile whose expected bone list a skeleton has (same canonical names and parents
    /// in the same order), or null. Rig A and the female rig share names but not order, so the order
    /// decides between them.
    /// </summary>
    public static RigProfile? FindFor(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        return FindFor(skeleton.Names, skeleton.Parents);
    }

    /// <summary>The built-in profile matching a bone list (see <see cref="FindFor(Skeleton)"/>), or null.</summary>
    public static RigProfile? FindFor(IReadOnlyList<string> boneNames, IReadOnlyList<int> parents)
    {
        ArgumentNullException.ThrowIfNull(boneNames);
        ArgumentNullException.ThrowIfNull(parents);
        foreach (var profile in BuiltIn)
        {
            if (Matches(profile, boneNames, parents)) return profile;
        }
        return null;
    }

    /// <summary>The built-in profile for a skeleton, or a <see cref="RigProfile.Generic(Skeleton)"/> one.</summary>
    public static RigProfile For(Skeleton skeleton) => FindFor(skeleton) ?? RigProfile.Generic(skeleton);

    /// <summary>
    /// True when the bone list is exactly the profile's expected one (names and parents, in order).
    /// Names compare through the profile's own rules, then without any exporter <c>xxxx-bdbn-</c>
    /// prefix, so a stock rig re-exported under another character prefix is still recognised.
    /// </summary>
    public static bool Matches(RigProfile profile, IReadOnlyList<string> boneNames, IReadOnlyList<int> parents)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(boneNames);
        ArgumentNullException.ThrowIfNull(parents);
        var bones = profile.Bones;
        if (bones.IsDefaultOrEmpty || bones.Length != boneNames.Count || parents.Count != boneNames.Count) return false;
        var names = new string[boneNames.Count];
        for (int i = 0; i < names.Length; i++)
        {
            string n = profile.Canonical(boneNames[i]);
            names[i] = n == bones[i].Name ? n : profile.Canonical(SkeletonMatcher.CanonicalBoneName(n));
        }
        for (int i = 0; i < bones.Length; i++)
        {
            if (names[i] != bones[i].Name) return false;
            int p = parents[i];
            string? parent = p >= 0 && p < names.Length ? names[p] : null;
            if (parent != bones[i].Parent) return false;
        }
        return true;
    }

    /// <summary>
    /// A name canonicalised with the stock rules (prefix and civilian table) after the generic
    /// exporter prefix: the "built-in tables" step of <see cref="BoneMapper"/>.
    /// </summary>
    internal static string StockCanonical(string boneName)
    {
        string n = RigA.Canonical(boneName);
        string g = SkeletonMatcher.CanonicalBoneName(n);
        return CivilianRenames.TryGetValue(g, out var renamed) ? renamed : g;
    }

    private static ImmutableArray<ProfileBone> ParseBones(string list) =>
    [
        .. list.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(entry =>
        {
            int colon = entry.IndexOf(':');
            string parent = entry[(colon + 1)..];
            return new ProfileBone(entry[..colon], parent.Length == 0 ? null : parent);
        }),
    ];
}
