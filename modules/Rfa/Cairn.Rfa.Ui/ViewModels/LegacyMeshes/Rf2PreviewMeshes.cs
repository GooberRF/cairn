using System.Collections.Concurrent;
using Cairn.Formats.Rf2;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// The Red Faction II character a Red Faction II clip plays on: the best of the <c>.rfc</c> meshes beside the clip (the
/// rest of its packfile, its folder) and, for a tab, of the Red Faction II install the clip belongs to, ranked by
/// <see cref="Rf2AnimationConverter.RankMeshes"/>, converted on the fly with its textures from the <c>.peg</c> packs beside
/// it. Everything here runs off the UI thread.
/// </summary>
internal static class Rf2PreviewMeshes
{
    /// <summary>A character to preview a clip on: the converted mesh, its name, the resolver of its textures and its fit.</summary>
    public sealed record Pick(string Name, V3dFile Mesh, AssetResolver? Textures, double JointOffset);

    /// <summary>One <c>.rfc</c> that may play a clip: its name, its skeleton, and how to read it whole and its neighbours.</summary>
    private sealed record Candidate(string Name, IReadOnlyList<V3dBone> Bones, Func<byte[]?> Read, IAssetSiblings? Siblings);

    /// <summary>The groups of an install that hold characters, read once per install (their skeletons only).</summary>
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<Candidate>>> s_installs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The groups whose characters a clip can play on: the levels' packfile, then the loose groups with meshes.</summary>
    private static readonly string[] CharacterGroups = ["All_Levels", "Animations_2", "Menu_extras", "Botmatch_core", "Loading_screen"];

    /// <summary>Bytes read from the start of a mesh to get its skeleton (spheres, props and bones come first).</summary>
    private const int HeadBytes = 64 * 1024;

    /// <summary>
    /// The best character for <paramref name="clip"/> among the <c>.rfc</c> in <paramref name="siblings"/> (the clip's
    /// packfile or folder), then, when none of them has the clip's bones and <paramref name="install"/> is known, among
    /// the characters of that Red Faction II install. Null when nothing fits or all fail to convert.
    /// </summary>
    /// <param name="clip">The clip (version 9).</param>
    /// <param name="clipName">Its file name (ranking by name).</param>
    /// <param name="siblings">The files beside the clip, or null.</param>
    /// <param name="install">The Red Faction II game folder the clip comes from, or null.</param>
    /// <param name="under">The shell's resolver, under the mesh's own textures.</param>
    /// <param name="ct">Cancels the search.</param>
    /// <param name="only">Only this character (the <c>--preview</c> diagnostic option), or null for the best.</param>
    /// <param name="known">
    /// The skeletons of the characters in <paramref name="siblings"/> when already known (the clip's packfile, read by the
    /// library's packfile group), so they are not read again; null to read them from the siblings.
    /// </param>
    public static Pick? Find(RfaClip clip, string clipName, IAssetSiblings? siblings, string? install, AssetResolver? under, CancellationToken ct, string? only = null,
        IReadOnlyList<(string Name, IReadOnlyList<V3dBone> Bones)>? known = null)
    {
        var near = siblings is null ? []
            : known is not null ? [.. known.Select(k => new Candidate(k.Name, k.Bones, () => siblings.Read(k.Name), siblings))]
            : FromSiblings(siblings, ct);
        if (TryBest(clip, clipName, near, under, only, ct) is { } pick) return pick;
        if (install is null) return null;
        var all = s_installs.GetOrAdd(install, path => new Lazy<IReadOnlyList<Candidate>>(() => FromInstall(path), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return TryBest(clip, clipName, all, under, only, ct);
    }

    /// <summary>
    /// The Red Faction II game folder a file belongs to: the parent of the nearest <c>pc_media</c> folder above it that
    /// holds packfile groups; null when there is none.
    /// </summary>
    public static string? InstallOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""); dir is not null; dir = dir.Parent)
            {
                if (string.Equals(dir.Name, "pc_media", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir.FullName, "All_Levels" + Rf2TocGroup.Extension)))
                    return dir.Parent?.FullName;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // not a path that leads to an install
        }
        return null;
    }

    private static Pick? TryBest(RfaClip clip, string clipName, IReadOnlyList<Candidate> candidates, AssetResolver? under, string? only, CancellationToken ct)
    {
        var byName = candidates.Where(c => c.Bones.Count == clip.BoneCount && (only is null || string.Equals(c.Name, only, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var ranked = Rf2AnimationConverter.RankMeshes(clip, clipName, byName.Values.Select(c => (c.Name, c.Bones)));
        // the best that converts (a damaged mesh is passed over)
        foreach (var (name, offset) in ranked.Take(4))
        {
            ct.ThrowIfCancellationRequested();
            var candidate = byName[name];
            if (candidate.Read() is not { } bytes) continue;
            try
            {
                var compiled = LegacyMeshSupport.ReadCompiled(bytes, name).Compiled;
                return new Pick(name, compiled, LegacyMeshTextures.PreviewResolver(bytes, name, candidate.Siblings, under), offset);
            }
            catch (AssetFormatException)
            {
                // the next best
            }
        }
        return null;
    }

    private static List<Candidate> FromSiblings(IAssetSiblings siblings, CancellationToken ct)
    {
        var list = new List<Candidate>();
        foreach (string name in siblings.Names.Where(n => n.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            // the skeleton comes first: the start of the file is enough, unless it is longer than that
            var head = siblings.ReadHead(name, HeadBytes);
            var bones = head is null ? null : LegacyMeshSupport.Rf2Skeleton(head);
            if (bones is null && head?.Length == HeadBytes && siblings.Read(name) is { } whole) bones = LegacyMeshSupport.Rf2Skeleton(whole);
            if (bones is { } b) list.Add(new Candidate(name, b, () => siblings.Read(name), siblings));
        }
        return list;
    }

    /// <summary>Every readable character of an install's groups, by its skeleton (read from the start of each file).</summary>
    private static IReadOnlyList<Candidate> FromInstall(string game)
    {
        var list = new List<Candidate>();
        string media = Path.Combine(game, "pc_media");
        foreach (string group in CharacterGroups)
        {
            string toc = Path.Combine(media, group + Rf2TocGroup.Extension);
            try
            {
                if (!File.Exists(toc)) continue;
                var siblings = new GroupSiblings(toc, Rf2TocGroup.Open(toc));
                foreach (string name in siblings.Names.Where(n => n.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase)))
                {
                    var head = siblings.Read(name, HeadBytes);
                    if (head is null) continue;
                    var bones = LegacyMeshSupport.Rf2Skeleton(head);
                    // a skeleton longer than the head: read it whole
                    if (bones is null && head.Length == HeadBytes && siblings.Read(name) is { } whole) bones = LegacyMeshSupport.Rf2Skeleton(whole);
                    if (bones is { } b) list.Add(new Candidate(name, b, () => siblings.Read(name), siblings));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException)
            {
                // a group that cannot be read gives no characters
            }
        }
        return list;
    }

    /// <summary>The files of a Red Faction II packfile group, by name: a packed group's from its <c>.packfile</c>, a loose one's from disk.</summary>
    private sealed class GroupSiblings : IAssetSiblings
    {
        private readonly string _toc;
        private readonly string? _packfile;
        private readonly Dictionary<string, Rf2TocEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public GroupSiblings(string toc, Rf2TocGroup group)
        {
            _toc = toc;
            _packfile = group.FindPackfile(toc);
            foreach (var e in group.Entries) _entries.TryAdd(e.Name, e);
            Label = Path.GetFileName(toc);
            Names = [.. _entries.Keys];
        }

        public string Label { get; }

        public IReadOnlyList<string> Names { get; }

        public bool Contains(string name) => !string.IsNullOrEmpty(name) && _entries.ContainsKey(Path.GetFileName(name));

        public byte[]? Read(string name) => Read(name, int.MaxValue);

        /// <summary>The first <paramref name="limit"/> bytes of the file (all of a shorter one), or null.</summary>
        public byte[]? Read(string name, int limit)
        {
            if (string.IsNullOrEmpty(name) || !_entries.TryGetValue(Path.GetFileName(name), out var e)) return null;
            try
            {
                string? path = e.StoredPath is null ? _packfile : Rf2TocGroup.LooseFilePath(_toc, e);
                if (path is null) return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (e.StoredPath is null) stream.Position = e.Offset;
                long size = e.StoredPath is null ? e.Size : stream.Length;
                var buffer = new byte[(int)Math.Min(Math.Min(size, limit), Array.MaxLength)];
                stream.ReadExactly(buffer);
                return buffer;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }
}
