using System.Collections.Immutable;
using System.Diagnostics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Assets;

/// <summary>What one open packfile offers the library: its entry names and how to read them (from any thread).</summary>
/// <param name="Key">The packfile's session key (one per tab).</param>
/// <param name="Label">Its name in lists ("All_Levels.toc_group").</param>
/// <param name="ArchivePath">Its file, or null when it was never saved.</param>
/// <param name="Names">Its entry names.</param>
/// <param name="ReadHead">The first n bytes of an entry (all of a shorter one), or null when it cannot be read.</param>
/// <param name="Read">A whole entry, or null when it cannot be read.</param>
public sealed record PackfileContents(string Key, string Label, string? ArchivePath, IReadOnlyList<string> Names,
    Func<string, int, byte[]?> ReadHead, Func<string, byte[]?> Read)
{
    /// <summary>True for a Red Faction II packfile group (its <c>.rfm</c> are Red Faction II meshes).</summary>
    public bool IsRedFactionII { get; init; }
}

/// <summary>
/// The clips and meshes of one open packfile, for the library's packfile groups and the preview pickers: the same
/// <see cref="LibraryClip"/> and <see cref="LibraryMesh"/> entries the library has, located in the packfile (an overlay
/// <see cref="AssetLocation"/> whose <see cref="AssetLocation.DisplayLocation"/> is the packfile's name), with skeleton
/// families over the characters. Red Faction II characters (<c>.rfc</c>) carry their skeleton, read from the start of
/// the file without the geometry (<see cref="LegacyMeshSupport.Rf2Skeleton"/>). Built once per packfile snapshot by
/// <see cref="Build"/> (headers only, off the UI thread, cancellable); immutable and safe to use from any thread.
/// </summary>
public sealed class PackfileLibrary
{
    /// <summary>The entry extensions a packfile group lists: clips, compiled meshes, and the meshes Cairn reads and converts.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".rfa", ".v3c", ".v3m", ".rfc", ".rfm", ".vcm", ".v3d"];

    /// <summary>Bytes read from the start of a Red Faction II character to get its skeleton (spheres, props and bones come first).</summary>
    public const int SkeletonHeadBytes = 64 * 1024;

    private readonly HashSet<object> _items = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _rf2Meshes;
    private readonly Dictionary<string, ImmutableArray<V3dBone>> _rf2Bones;
    private readonly Dictionary<string, LibraryClip> _clipByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LibraryMesh> _meshByName = new(StringComparer.OrdinalIgnoreCase);

    private PackfileLibrary(PackfileContents contents, ImmutableArray<LibraryClip> clips, ImmutableArray<LibraryMesh> meshes,
        HashSet<string> rf2Meshes, Dictionary<string, ImmutableArray<V3dBone>> rf2Bones, int errors, TimeSpan probeTime)
    {
        Contents = contents;
        Clips = clips;
        Meshes = meshes;
        _rf2Meshes = rf2Meshes;
        _rf2Bones = rf2Bones;
        Errors = errors;
        ProbeTime = probeTime;
        foreach (var c in clips) { _items.Add(c); _clipByName.TryAdd(c.Name, c); _clipByName.TryAdd(c.BaseName, c); s_tags.AddOrUpdate(c, new ItemTag(contents.Key, false)); }
        foreach (var m in meshes) { _items.Add(m); _meshByName.TryAdd(m.Name, m); s_tags.AddOrUpdate(m, new ItemTag(contents.Key, rf2Meshes.Contains(m.Name))); }
        // one family list per game: a Red Faction II skeleton is never a Red Faction one, whatever its bone names
        IEnumerable<SkeletonFamily> FamiliesOf(bool rf2) => SkeletonMatcher.GroupFamilies(meshes
            .Where(m => m.HasSkeleton && _rf2Meshes.Contains(m.Name) == rf2)
            .Select(m => new SkeletonFamilyMember(m.Name, m.Facts!.BoneNames, m.Facts.BoneParents)));
        Families = [.. FamiliesOf(false), .. FamiliesOf(true)];
    }

    /// <summary>What the packfile offered.</summary>
    public PackfileContents Contents { get; }

    /// <summary>The packfile's session key.</summary>
    public string Key => Contents.Key;

    /// <summary>The packfile's name ("All_Levels.toc_group").</summary>
    public string Label => Contents.Label;

    /// <summary>The packfile's file, or null when it was never saved.</summary>
    public string? ArchivePath => Contents.ArchivePath;

    /// <summary>The clips, by name.</summary>
    public ImmutableArray<LibraryClip> Clips { get; }

    /// <summary>The meshes (characters and static meshes), by name.</summary>
    public ImmutableArray<LibraryMesh> Meshes { get; }

    /// <summary>Skeleton families over the characters: the Red Faction ones, then the Red Faction II ones.</summary>
    public ImmutableArray<SkeletonFamily> Families { get; }

    /// <summary>Meshes without bones (static meshes, and any mesh that could not be read), by name.</summary>
    public IEnumerable<LibraryMesh> StaticMeshes => Meshes.Where(m => !m.HasSkeleton);

    /// <summary>Entries that could not be read.</summary>
    public int Errors { get; }

    /// <summary>How long <see cref="Build"/> took.</summary>
    public TimeSpan ProbeTime { get; }

    /// <summary>True when the packfile holds no clip and no mesh.</summary>
    public bool IsEmpty => Clips.IsEmpty && Meshes.IsEmpty;

    /// <summary>"12 meshes · 60 clips".</summary>
    public string Summary => $"{Count(Meshes.Length, "mesh", "meshes")} · {Count(Clips.Length, "clip", "clips")}";

    private static string Count(int n, string one, string many) => n == 1 ? "1 " + one : $"{n:N0} {many}";

    /// <summary>True when <paramref name="item"/> is one of this packfile's <see cref="Clips"/> or <see cref="Meshes"/> (by reference).</summary>
    public bool Contains(object? item) => item is not null && _items.Contains(item);

    /// <summary>True when <paramref name="mesh"/> is a Red Faction II mesh of this packfile (a character or a static mesh).</summary>
    public bool IsRedFactionII(LibraryMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return _rf2Meshes.Contains(mesh.Name);
    }

    /// <summary>What every item read from a packfile carries, whether its snapshot is still open or not (by reference).</summary>
    private sealed record ItemTag(string Key, bool IsRedFactionII);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ItemTag> s_tags = new();

    /// <summary>
    /// The session key of the packfile <paramref name="item"/> (one of a packfile library's clips or meshes, by reference)
    /// was read from, also after that packfile changed or closed; null for any other item (the search-path library's).
    /// </summary>
    public static string? KeyOf(object? item) => item is not null && s_tags.TryGetValue(item, out var tag) ? tag.Key : null;

    /// <summary>
    /// True for a Red Faction II mesh read from a packfile, whether or not that packfile snapshot is still open (an item a
    /// library row, a drag or a menu held from before the packfile changed keeps its game); false for any other mesh.
    /// </summary>
    public static bool IsRedFactionIIMesh(LibraryMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return s_tags.TryGetValue(mesh, out var tag) && tag.IsRedFactionII;
    }

    /// <summary>True for a Red Faction II clip (version 9).</summary>
    public static bool IsRedFactionII(LibraryClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Facts?.IsRedFactionII == true;
    }

    /// <summary>The skeleton of a Red Faction II character of this packfile as its conversion stores it, or null.</summary>
    public ImmutableArray<V3dBone>? Rf2Bones(string meshName) => _rf2Bones.TryGetValue(meshName, out var bones) ? bones : null;

    /// <summary>The mesh named <paramref name="name"/> (file name), or null.</summary>
    public LibraryMesh? FindMesh(string name) => !string.IsNullOrWhiteSpace(name) && _meshByName.TryGetValue(Path.GetFileName(name.Trim()), out var m) ? m : null;

    /// <summary>The clip named <paramref name="name"/> (file name or base name), or null.</summary>
    public LibraryClip? FindClip(string name) => !string.IsNullOrWhiteSpace(name) && _clipByName.TryGetValue(Path.GetFileName(name.Trim()), out var c) ? c : null;

    /// <summary>
    /// The Red Faction II characters of this packfile that play <paramref name="clip"/> (its bone count), best first by
    /// <see cref="Rf2AnimationConverter.RankMeshes"/> (name, then bind-pose fit), each with its joint offset.
    /// </summary>
    public IReadOnlyList<(LibraryMesh Mesh, double JointOffset)> RankRf2Meshes(RfaClip clip, string clipName)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(clipName);
        var candidates = _rf2Bones.Where(p => p.Value.Length == clip.BoneCount).Select(p => (p.Key, (IReadOnlyList<V3dBone>)p.Value));
        return [.. Rf2AnimationConverter.RankMeshes(clip, clipName, candidates)
            .Select(r => (FindMesh(r.Name), r.JointOffset)).Where(r => r.Item1 is not null).Select(r => (r.Item1!, r.JointOffset))];
    }

    /// <summary>
    /// The characters of this packfile a clip of the given game could play on, for a picker: those with
    /// <paramref name="boneCount"/> bones first, then the others, each by name; the other game's are never included.
    /// </summary>
    public IReadOnlyList<LibraryMesh> CharactersFor(int boneCount, bool redFactionII) =>
        [.. Meshes.Where(m => m.HasSkeleton && IsRedFactionII(m) == redFactionII).OrderBy(m => m.BoneCount == boneCount ? 0 : 1)];

    /// <summary>The readable clips of the given game with <paramref name="boneCount"/> bones, by name.</summary>
    public IReadOnlyList<LibraryClip> ClipsFor(int boneCount, bool redFactionII) =>
        [.. Clips.Where(c => c.IsReadable && IsRedFactionII(c) == redFactionII && c.BoneCount == boneCount)];

    // ── Building ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Probes the clips and meshes of <paramref name="contents"/>: a clip's header (<see cref="RfaProbe"/>), a compiled or
    /// exporter mesh as the library does (<see cref="V3dProbe"/>), a Red Faction II character's skeleton from the start of
    /// the file, a PlayStation 2 character through its reader; a Red Faction II static mesh (<c>.rfm</c>) is listed
    /// without being read. An entry that cannot be read becomes one with an error, never an exception. Runs on the calling
    /// thread (several entries at once).
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static PackfileLibrary Build(PackfileContents contents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var clock = Stopwatch.StartNew();
        var names = contents.Names.Where(n => Extensions.Contains(Path.GetExtension(n), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var nameSet = new HashSet<string>(contents.Names.Select(n => Path.GetFileName(n)), StringComparer.OrdinalIgnoreCase);
        var overlay = new AssetResolver(new AssetResolverOptions()).WithOverlay(contents.Label, n => nameSet.Contains(Path.GetFileName(n)),
            n => contents.Read(n) is { } bytes ? new MemoryStream(bytes, writable: false) : null);

        var results = new Probe[names.Count];
        Parallel.For(0, names.Count, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4) },
            i => results[i] = ProbeOne(contents, names[i], cancellationToken));

        var clips = ImmutableArray.CreateBuilder<LibraryClip>();
        var meshes = ImmutableArray.CreateBuilder<LibraryMesh>();
        var rf2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bones = new Dictionary<string, ImmutableArray<V3dBone>>(StringComparer.OrdinalIgnoreCase);
        int errors = 0;
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            var p = results[i];
            var location = overlay.Resolve(name, cancellationToken) ?? new AssetLocation(name, name, AssetSourceKind.DocumentFolder, null, null, null) { Origin = contents.Label };
            if (p.Error is not null) errors++;
            if (name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) clips.Add(new LibraryClip(name, location, p.Clip, p.Error, null));
            else
            {
                meshes.Add(new LibraryMesh(name, location, p.Mesh, p.Error, null));
                if (p.RedFactionII) rf2.Add(name);
                if (p.Rf2Bones is { } b) bones[name] = b;
            }
        }
        return new PackfileLibrary(contents, clips.ToImmutable(), meshes.ToImmutable(), rf2, bones, errors, clock.Elapsed);
    }

    private sealed record Probe(RfaProbeResult? Clip, V3dProbeResult? Mesh, string? Error, bool RedFactionII = false, ImmutableArray<V3dBone>? Rf2Bones = null);

    private static Probe ProbeOne(PackfileContents contents, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string ext = Path.GetExtension(name).ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case ".rfa":
                {
                    var head = contents.ReadHead(name, RfaClip.BoneTableOffset) ?? throw new IOException("it could not be read");
                    return new Probe(RfaProbe.Probe(head, name), null, null);
                }
                case ".rfm":
                    // a static mesh: nothing a clip plays on, so it is listed without being read
                    return new Probe(null, StaticFacts, null, contents.IsRedFactionII);
                case ".rfc":
                {
                    var head = contents.ReadHead(name, SkeletonHeadBytes) ?? throw new IOException("it could not be read");
                    if (LegacyMeshSupport.IsRedFactionII(head))
                    {
                        var skeleton = LegacyMeshSupport.Rf2Skeleton(head);
                        // a skeleton longer than the head: read it whole
                        if (skeleton is null && head.Length == SkeletonHeadBytes && contents.Read(name) is { } whole) skeleton = LegacyMeshSupport.Rf2Skeleton(whole);
                        if (skeleton is not { } rf2Bones) return new Probe(null, null, $"'{name}' has no readable Red Faction II skeleton.", true);
                        var facts = new V3dProbeResult(V3dKind.Character, [], [], [.. rf2Bones.Select(b => b.Name.Text)], [.. rf2Bones.Select(b => b.ParentIndex)], 0, true);
                        return new Probe(null, facts, null, true, rf2Bones);
                    }
                    return LegacyCharacter(contents, name);
                }
                case ".vcm" or ".v3d":
                {
                    var bytes = contents.Read(name) ?? throw new IOException("it could not be read");
                    if (LegacyMeshSupport.IsLegacyContent(bytes) && !ExporterMeshReader.IsExporterLayout(bytes)) return LegacyCharacter(contents, name, bytes);
                    return new Probe(null, V3dProbe.Probe(bytes, name), null);
                }
                default:
                {
                    var bytes = contents.Read(name) ?? throw new IOException("it could not be read");
                    if (bytes.Length > FileBytes.MaxAssetBytes)
                        throw new AssetFormatException($"'{name}' is larger than {FileBytes.MaxAssetBytes / (1024 * 1024)} MB, which no mesh is.");
                    return new Probe(null, V3dProbe.Probe(bytes, name), null);
                }
            }
        }
        catch (AssetFormatException ex)
        {
            return new Probe(null, null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or IndexOutOfRangeException or OverflowException)
        {
            return new Probe(null, null, $"'{name}' could not be read: {ex.Message}");
        }
    }

    /// <summary>A PlayStation 2 mesh (Red Faction): its bones through its reader (the whole file is read).</summary>
    private static Probe LegacyCharacter(PackfileContents contents, string name, byte[]? bytes = null)
    {
        bytes ??= contents.Read(name) ?? throw new IOException("it could not be read");
        if (!LegacyMeshSupport.TryRead(bytes, name, out var mesh, out var error) || mesh is null)
            return new Probe(null, null, error?.Message ?? $"'{name}' could not be read.");
        var d = mesh.Description;
        var facts = new V3dProbeResult(d.Kind, [.. d.Submeshes.Select(s => s.Name.Text)], [.. d.Submeshes.Select(s => s.Lods.Length)],
            [.. d.Bones.Select(b => b.Name.Text)], [.. d.Bones.Select(b => b.ParentIndex)], d.CollisionSpheres.Length, true);
        return new Probe(null, facts, null, mesh.IsRedFactionII);
    }

    private static readonly V3dProbeResult StaticFacts = new(V3dKind.StaticMesh, [], [], [], [], 0, true);
}

/// <summary>
/// Which game a clip or mesh belongs to, and why a clip of one cannot play on a mesh of the other: a clip stores bone
/// indices only, so a Red Faction II clip on a Red Faction mesh with as many bones (or the reverse) plays mangled.
/// </summary>
public static class AssetGames
{
    /// <summary>"Red Faction II" or "Red Faction".</summary>
    public static string Name(bool redFactionII) => redFactionII ? "Red Faction II" : "Red Faction";

    /// <summary>
    /// Null when the clip and the mesh are of the same game; otherwise why the clip will not play on it
    /// ("guard_swing.rfa is a Red Faction II clip and miner.v3c a Red Faction mesh: ...").
    /// </summary>
    public static string? Mismatch(string clipName, bool clipIsRedFactionII, string meshName, bool meshIsRedFactionII) =>
        clipIsRedFactionII == meshIsRedFactionII ? null
            : $"{clipName} is a {Name(clipIsRedFactionII)} clip and {meshName} a {Name(meshIsRedFactionII)} mesh: a clip stores bone indices only, so it would play mangled";
}
