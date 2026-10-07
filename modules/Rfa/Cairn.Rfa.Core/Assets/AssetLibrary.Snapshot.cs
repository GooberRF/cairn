using System.Collections.Immutable;
using Cairn.Rfa.Animation;
using Cairn.Formats.Tbl;

namespace Cairn.Rfa.Assets;

/// <summary>
/// One immutable state of the <see cref="AssetLibrary"/>: every clip and mesh the resolver can see,
/// the copies the engine loads and the ones they shadow, skeleton families, and the queries the UI
/// needs. A build produces a new snapshot; readers keep whichever one they took and never see a
/// half-built index. Safe to use from any thread.
/// </summary>
public sealed class LibrarySnapshot
{
    private readonly Dictionary<string, List<LibraryClip>> _clipCopies;
    private readonly Dictionary<string, List<LibraryMesh>> _meshCopies;
    private readonly Dictionary<string, SkeletonFamily> _familyByMesh;

    internal LibrarySnapshot(
        ImmutableArray<LibraryClip> allClips, ImmutableArray<LibraryMesh> allMeshes, LibraryBuildStats stats, DateTime builtUtc)
    {
        AllClips = allClips;
        AllMeshes = allMeshes;
        Stats = stats;
        BuiltUtc = builtUtc;

        _clipCopies = new Dictionary<string, List<LibraryClip>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in allClips) Add(_clipCopies, c.BaseName, c);
        _meshCopies = new Dictionary<string, List<LibraryMesh>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in allMeshes) Add(_meshCopies, m.Name, m);

        Clips = [.. allClips.Where(c => !c.IsShadowed).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
        Meshes = [.. allMeshes.Where(m => !m.IsShadowed).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];

        Families = [.. SkeletonMatcher.GroupFamilies(Meshes
            .Where(m => m.HasSkeleton)
            .Select(m => new SkeletonFamilyMember(m.Name, m.Facts!.BoneNames, m.Facts.BoneParents)))];
        _familyByMesh = new Dictionary<string, SkeletonFamily>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Families)
        {
            foreach (string member in family.Members) _familyByMesh[member] = family;
        }
    }

    /// <summary>A library with nothing in it (before the first build).</summary>
    public static LibrarySnapshot Empty { get; } = new([], [], LibraryBuildStats.None, DateTime.MinValue);

    /// <summary>The clips the engine would load (one per name), sorted by name.</summary>
    public ImmutableArray<LibraryClip> Clips { get; }

    /// <summary>The meshes the engine would load (one per name), sorted by name.</summary>
    public ImmutableArray<LibraryMesh> Meshes { get; }

    /// <summary>Every clip location in search order, shadowed copies included.</summary>
    public ImmutableArray<LibraryClip> AllClips { get; }

    /// <summary>Every mesh location in search order, shadowed copies included.</summary>
    public ImmutableArray<LibraryMesh> AllMeshes { get; }

    /// <summary>
    /// Skeleton families over the winning meshes with bones (identical canonical bone lists, see
    /// <see cref="SkeletonMatcher"/>); member ids are mesh file names. Any clip for one member plays on all.
    /// </summary>
    public ImmutableArray<SkeletonFamily> Families { get; }

    /// <summary>Winning meshes without bones (static meshes and unreadable files), sorted by name.</summary>
    public IEnumerable<LibraryMesh> StaticMeshes => Meshes.Where(m => !m.HasSkeleton);

    /// <summary>What the build that produced this snapshot did.</summary>
    public LibraryBuildStats Stats { get; }

    /// <summary>When the snapshot was built (UTC); <see cref="DateTime.MinValue"/> for <see cref="Empty"/>.</summary>
    public DateTime BuiltUtc { get; }

    // ── Lookups ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The clip the engine would load for <paramref name="clipName"/> — any spelling: <c>x</c>,
    /// <c>x.rfa</c>, <c>x.mvf</c>, with or without a folder (clip identity is the base name) — or null.
    /// </summary>
    public LibraryClip? FindClip(string clipName) => CopiesOfClip(clipName) is [var winner, ..] ? winner : null;

    /// <summary>Every copy of a clip in search order: the loaded one first, then the ones it shadows.</summary>
    public IReadOnlyList<LibraryClip> CopiesOfClip(string clipName) =>
        string.IsNullOrWhiteSpace(clipName) ? [] : _clipCopies.TryGetValue(ClipUsageIndex.ClipKey(clipName), out var list) ? list.AsReadOnly() : [];

    /// <summary>
    /// The mesh the engine would load for <paramref name="meshName"/> — table spellings work
    /// (<c>miner.vcm</c>, <c>fp_glock.v3d</c>, which tries <c>.v3m</c> then <c>.v3c</c>); a name without
    /// an extension tries <c>.v3c</c> then <c>.v3m</c> — or null.
    /// </summary>
    public LibraryMesh? FindMesh(string meshName) => CopiesOfMesh(meshName) is [var winner, ..] ? winner : null;

    /// <summary>Every copy of a mesh in search order: the loaded one first, then the ones it shadows.</summary>
    public IReadOnlyList<LibraryMesh> CopiesOfMesh(string meshName)
    {
        foreach (string candidate in MeshCandidates(meshName))
        {
            if (_meshCopies.TryGetValue(candidate, out var list)) return list.AsReadOnly();
        }
        return [];
    }

    /// <summary>The skeleton family of a winning mesh, or null (static, unreadable or unknown mesh).</summary>
    public SkeletonFamily? FamilyOf(string meshName) =>
        FindMesh(meshName) is { } mesh && _familyByMesh.TryGetValue(mesh.Name, out var family) ? family : null;

    // ── Filters ──────────────────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="name"/> matches a filter: empty matches everything; a pattern with
    /// <c>*</c> or <c>?</c> is a wildcard matched against the whole file name or its base name; anything
    /// else is a substring. Case-insensitive.
    /// </summary>
    public static bool Matches(string name, string? pattern)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        string p = pattern.Trim();
        if (p.IndexOfAny(['*', '?']) < 0) return name.Contains(p, StringComparison.OrdinalIgnoreCase);
        return Wildcard(name, p) || Wildcard(Path.GetFileNameWithoutExtension(name), p);
    }

    /// <summary>Clips whose name matches <paramref name="pattern"/> (see <see cref="Matches"/>), loaded copies only unless asked.</summary>
    public IReadOnlyList<LibraryClip> FilterClips(string? pattern, bool includeShadowed = false) =>
        [.. (includeShadowed ? AllClips.AsEnumerable() : Clips).Where(c => Matches(c.Name, pattern))];

    /// <summary>Meshes whose name matches <paramref name="pattern"/> (see <see cref="Matches"/>), loaded copies only unless asked.</summary>
    public IReadOnlyList<LibraryMesh> FilterMeshes(string? pattern, bool includeShadowed = false) =>
        [.. (includeShadowed ? AllMeshes.AsEnumerable() : Meshes).Where(m => Matches(m.Name, pattern))];

    /// <summary>How a readable clip fits a mesh with <paramref name="meshBoneCount"/> bones, or null when the clip is unreadable.</summary>
    public static ClipFit? Fit(LibraryClip clip, int meshBoneCount)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Facts is { } facts ? SkeletonMatcher.Check(meshBoneCount, facts.BoneCount) : null;
    }

    /// <summary>Loaded clips with exactly <paramref name="meshBoneCount"/> bones (they play on such a mesh), sorted by name.</summary>
    public IReadOnlyList<LibraryClip> CompatibleClips(int meshBoneCount) =>
        meshBoneCount <= 0 ? [] : [.. Clips.Where(c => Fit(c, meshBoneCount) == ClipFit.Match)];

    /// <summary>Loaded clips whose bone count matches <paramref name="skeleton"/>.</summary>
    public IReadOnlyList<LibraryClip> CompatibleClips(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        return CompatibleClips(skeleton.Count);
    }

    /// <summary>Loaded clips whose bone count matches <paramref name="mesh"/>'s.</summary>
    public IReadOnlyList<LibraryClip> CompatibleClips(LibraryMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return CompatibleClips(mesh.BoneCount);
    }

    /// <summary>Loaded meshes with exactly <paramref name="boneCount"/> bones, sorted by name.</summary>
    public IReadOnlyList<LibraryMesh> CompatibleMeshes(int boneCount) =>
        boneCount <= 0 ? [] : [.. Meshes.Where(m => m.BoneCount == boneCount)];

    // ── Table-aware queries ──────────────────────────────────────────────────

    /// <summary>
    /// The clips the tables give <paramref name="meshName"/>, route by route, each with the library's
    /// loaded copy (null when no searched location has the clip).
    /// </summary>
    public IReadOnlyList<LibraryClipUsage> ClipsUsedByMesh(string meshName, ClipUsageIndex usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var result = new List<LibraryClipUsage>();
        foreach (var list in usage.ClipListsForMesh(meshName))
        {
            foreach (var u in list.Clips) result.Add(new LibraryClipUsage(list, u, FindClip(u.ClipBaseName)));
        }
        return result;
    }

    /// <summary>
    /// The mesh to preview a clip on (DESIGN 5): a mesh the tables play it on (one whose bone count
    /// matches first), else <paramref name="lastUsedMesh"/> when the library has it and its bone count
    /// does not contradict the clip's, else the first loaded mesh whose bone count matches. Null when
    /// nothing fits.
    /// </summary>
    public LibraryMesh? DefaultPreviewMesh(string clipName, ClipUsageIndex? usage = null, string? lastUsedMesh = null)
    {
        ArgumentNullException.ThrowIfNull(clipName);
        int? bones = FindClip(clipName)?.BoneCount;

        if (usage is not null)
        {
            var tableMeshes = usage.MeshesForClip(clipName).Select(FindMesh).OfType<LibraryMesh>().Where(m => m.HasSkeleton).ToList();
            var pick = tableMeshes.FirstOrDefault(m => bones is null || m.BoneCount == bones) ?? tableMeshes.FirstOrDefault();
            if (pick is not null) return pick;
        }

        if (!string.IsNullOrWhiteSpace(lastUsedMesh) && FindMesh(lastUsedMesh) is { HasSkeleton: true } last
            && (bones is null || last.BoneCount == bones))
        {
            return last;
        }

        return bones is { } count ? Meshes.FirstOrDefault(m => m.BoneCount == count) : null;
    }

    /// <summary>
    /// Clips for a mesh's preview picker: compatible clips (bone count matches), those the tables give
    /// this mesh on top (in table order), then other compatible clips the tables give the mesh's skeleton
    /// family, then the rest by name; incompatible clips the tables name follow when
    /// <paramref name="includeIncompatible"/> is set.
    /// </summary>
    public IReadOnlyList<LibraryClip> PreviewClipCandidates(string meshName, ClipUsageIndex? usage = null, bool includeIncompatible = false)
    {
        ArgumentNullException.ThrowIfNull(meshName);
        var mesh = FindMesh(meshName);
        int bones = mesh?.BoneCount ?? 0;
        var seen = new HashSet<LibraryClip>(ReferenceEqualityComparer.Instance);
        var top = new List<LibraryClip>();
        var incompatible = new List<LibraryClip>();

        if (usage is not null)
        {
            foreach (var u in ClipsUsedByMesh(mesh?.Name ?? meshName, usage))
            {
                if (u.Clip is not { } clip || !seen.Add(clip)) continue;
                if (bones > 0 && clip.BoneCount == bones) top.Add(clip);
                else incompatible.Add(clip);
            }
        }

        var compatible = CompatibleClips(bones).Where(c => !seen.Contains(c)).ToList();
        if (usage is not null && mesh is not null && FamilyOf(mesh.Name) is { } family)
        {
            var familyClips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string member in family.Members)
            {
                foreach (var list in usage.ClipListsForMesh(member))
                {
                    foreach (var u in list.Clips) familyClips.Add(u.ClipBaseName);
                }
            }
            compatible = [.. compatible.OrderBy(c => familyClips.Contains(c.BaseName) ? 0 : 1)];
        }

        var result = new List<LibraryClip>(top.Count + compatible.Count);
        result.AddRange(top);
        result.AddRange(compatible);
        if (includeIncompatible) result.AddRange(incompatible);
        return result;
    }

    /// <summary>
    /// The clip to preview a mesh with: among <see cref="PreviewClipCandidates"/> (compatible, table
    /// usage first), a stand / idle clip is preferred — a table slot named "stand" or "idle" first, then
    /// a clip whose name contains "stand", then "idle" — else the first candidate. Null when no clip fits.
    /// </summary>
    public LibraryClip? DefaultPreviewClip(string meshName, ClipUsageIndex? usage = null)
    {
        ArgumentNullException.ThrowIfNull(meshName);
        var candidates = PreviewClipCandidates(meshName, usage);
        if (candidates.Count == 0) return null;

        if (usage is not null && FindMesh(meshName) is { } mesh)
        {
            var bySlot = ClipsUsedByMesh(mesh.Name, usage)
                .Where(u => u.Clip is not null && u.Usage.WeaponBlock is null
                    && (u.Usage.SlotName.Equals("stand", StringComparison.OrdinalIgnoreCase)
                        || u.Usage.SlotName.Equals("idle", StringComparison.OrdinalIgnoreCase)))
                .Select(u => u.Clip!)
                .FirstOrDefault(c => candidates.Contains(c));
            if (bySlot is not null) return bySlot;
        }

        return candidates.FirstOrDefault(c => c.BaseName.Contains("stand", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(c => c.BaseName.Contains("idle", StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<string> MeshCandidates(string meshName)
    {
        if (string.IsNullOrWhiteSpace(meshName)) yield break;
        string bare = Path.GetFileName(meshName.Trim().Replace('/', '\\'));
        if (Path.GetExtension(bare).Length == 0)
        {
            yield return bare + ".v3c";
            yield return bare + ".v3m";
            yield break;
        }
        foreach (string c in TblFileName.Normalize(bare)) yield return c;
    }

    private static bool Wildcard(string text, string pattern)
    {
        // Iterative glob match with backtracking to the last '*'.
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }
}
