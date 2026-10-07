using System.Collections.Immutable;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Animation;

/// <summary>How a clip's bone count relates to a mesh's.</summary>
public enum ClipFit
{
    /// <summary>Same count: the clip plays (bones are matched by index).</summary>
    Match,
    /// <summary>The clip has fewer bones: the engine reads past its bone table for the rest.</summary>
    ClipHasFewerBones,
    /// <summary>The clip has more bones: the extra tracks are ignored, the rest are probably the wrong bones.</summary>
    ClipHasMoreBones,
}

/// <summary>One mesh offered for family grouping.</summary>
/// <param name="Id">The caller's key for the mesh (a path or library id).</param>
/// <param name="BoneNames">Bone names in index order.</param>
/// <param name="BoneParents">Parent index of each bone.</param>
public sealed record SkeletonFamilyMember(string Id, ImmutableArray<string> BoneNames, ImmutableArray<int> BoneParents);

/// <summary>Meshes sharing one bone list: any clip made for one plays on all of them.</summary>
/// <param name="BoneNames">The shared bone names (spelled as the first member has them).</param>
/// <param name="BoneParents">The shared parent indices.</param>
/// <param name="Members">Member ids in the order they were offered.</param>
public sealed record SkeletonFamily(ImmutableArray<string> BoneNames, ImmutableArray<int> BoneParents, ImmutableArray<string> Members)
{
    /// <summary>Number of bones.</summary>
    public int BoneCount => BoneNames.Length;
}

/// <summary>
/// Clip-to-mesh compatibility and skeleton families. The engine matches bones by index and never
/// compares counts, so the only hard check is the count; families group meshes whose bone lists are
/// identical — same order, same parents, same <see cref="CanonicalBoneName">canonical names</see> —
/// which is when a clip made for one is right for the others. Canonical names drop the exporter's
/// per-character prefix (<c>ult2-bdbn-head</c> and <c>park-bdbn-head</c> are both <c>head</c>), as the
/// research rigs do (rigs.py), so miner.v3c and ult2_guard.v3c are one family.
/// </summary>
public static class SkeletonMatcher
{
    private static readonly System.Text.RegularExpressions.Regex ExporterPrefix =
        new(@"^[a-z0-9]+-bdbn-", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A bone name with case folded and the exporter's <c>xxxx-bdbn-</c> character prefix removed.
    /// </summary>
    public static string CanonicalBoneName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ExporterPrefix.Replace(name.Trim(), string.Empty).ToLowerInvariant();
    }

    /// <summary>Compares a clip's bone count with a mesh's.</summary>
    public static ClipFit Check(int meshBones, int clipBones) =>
        clipBones == meshBones ? ClipFit.Match : clipBones < meshBones ? ClipFit.ClipHasFewerBones : ClipFit.ClipHasMoreBones;

    /// <summary>Compares a clip with a skeleton.</summary>
    public static ClipFit Check(Skeleton skeleton, RfaClip clip)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(clip);
        return Check(skeleton.Count, clip.BoneCount);
    }

    /// <summary>True when the clip's bone count equals the skeleton's.</summary>
    public static bool Fits(Skeleton skeleton, RfaClip clip) => Check(skeleton, clip) == ClipFit.Match;

    /// <summary>True when two bone lists are identical (canonical names, parents, order).</summary>
    public static bool SameBoneList(
        IReadOnlyList<string> namesA, IReadOnlyList<int> parentsA, IReadOnlyList<string> namesB, IReadOnlyList<int> parentsB)
    {
        if (namesA.Count != namesB.Count || parentsA.Count != namesA.Count || parentsB.Count != namesB.Count) return false;
        for (int i = 0; i < namesA.Count; i++)
        {
            if (parentsA[i] != parentsB[i]) return false;
            if (CanonicalBoneName(namesA[i]) != CanonicalBoneName(namesB[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// Groups meshes into skeleton families. Meshes without bones are left out (static meshes are
    /// shown apart). Families come back in the order their first member was offered.
    /// </summary>
    public static IReadOnlyList<SkeletonFamily> GroupFamilies(IEnumerable<SkeletonFamilyMember> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        var families = new List<(SkeletonFamilyMember First, List<string> Members)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var mesh in meshes)
        {
            if (mesh.BoneNames.IsDefaultOrEmpty) continue;
            string key = Key(mesh.BoneNames, mesh.BoneParents);
            if (index.TryGetValue(key, out int at))
            {
                families[at].Members.Add(mesh.Id);
            }
            else
            {
                index[key] = families.Count;
                families.Add((mesh, [mesh.Id]));
            }
        }
        return [.. families.Select(f => new SkeletonFamily(f.First.BoneNames, f.First.BoneParents, [.. f.Members]))];
    }

    private static string Key(ImmutableArray<string> names, ImmutableArray<int> parents)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < names.Length; i++)
        {
            int parent = parents.IsDefault || i >= parents.Length ? int.MinValue : parents[i];
            sb.Append(CanonicalBoneName(names[i])).Append('\u0001').Append(parent).Append('\u0002');
        }
        return sb.ToString();
    }
}
