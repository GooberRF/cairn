using System.Collections.Concurrent;
using Cairn.Assets;
using Cairn.Formats.Rf2;
using Cairn.Lvl.Conversion;
using Cairn.Ui.Modules;

namespace Cairn.Lvl.Ui.Preview;

/// <summary>
/// Where a level finds its textures: a Red Faction II level's are inside the <c>.peg</c> packs beside it (the rest of
/// its packfile, or its folder), <c>&lt;level&gt;.peg</c> first, then the shared packs of its install's
/// <c>Permanent_data</c> group when the install is known; every level then looks beside it and in the game data.
/// </summary>
internal static class LvlTexturePacks
{
    // An install's shared packs and clutter table, read once per install (the readers open the packfile only on demand).
    private static readonly ConcurrentDictionary<string, IReadOnlyList<PegPack>> Shared = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Rf2ClutterClass>?> Clutter = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The resolver for <paramref name="source"/>: for a Red Faction II level the textures in its packs and the files
    /// beside it, then <paramref name="under"/>; for any other level <paramref name="under"/> itself (which already
    /// looks beside the file).
    /// </summary>
    /// <param name="source">The level.</param>
    /// <param name="filePath">The level's file on disk, or null.</param>
    /// <param name="siblings">The files beside it (its packfile's other entries), or null.</param>
    /// <param name="under">The resolver used otherwise (the document's or the preview's).</param>
    /// <param name="rf2Install">The Red Faction II install the level belongs to (see <see cref="Rf2InstallOf"/>), or null.</param>
    public static AssetResolver? ResolverFor(LvlSource source, string? filePath, IAssetSiblings? siblings, AssetResolver? under, string? rf2Install = null)
    {
        if (source.Level is not { IsRf2: true }) return under;
        var packs = PacksFor(source.Name, filePath, siblings, rf2Install);
        if (packs is null || packs.IsEmpty) return under;
        var baseResolver = under ?? new AssetResolver(new AssetResolverOptions());
        if (siblings is null) return packs.Layer(baseResolver);
        // a resolver keeps one in-memory layer: the siblings and the packs' textures are joined in it
        return baseResolver.WithOverlay(siblings.Label,
            name => siblings.Contains(name) || packs.Offers(name),
            name => siblings.Contains(name)
                ? siblings.Read(name) is { } bytes ? new MemoryStream(bytes, writable: false) : null
                : packs.Open(name));
    }

    /// <summary>
    /// The <c>.peg</c> packs of a Red Faction II level, in search order (its own packs beside it, then the shared packs of
    /// <paramref name="rf2Install"/>), or null when there is nowhere to look.
    /// </summary>
    public static PegTextureSet? PacksFor(string levelName, string? filePath, IAssetSiblings? siblings, string? rf2Install = null)
    {
        string label = $"the .peg packs beside {Path.GetFileName(levelName)}";
        var shared = rf2Install is null ? [] : SharedPacks(rf2Install);
        IEnumerable<PegPack> own;
        if (siblings is not null)
        {
            label += $" ({siblings.Label})";
            own = PegTextureSet.SearchOrder(Path.GetFileName(levelName), siblings.Names, [""]).Select(n => new PegPack(n, () => siblings.Read(n)));
        }
        else if (filePath is not null)
        {
            string? folder = Path.GetDirectoryName(filePath);
            var names = new List<string>();
            try
            {
                if (folder is not null && Directory.Exists(folder))
                    names.AddRange(Directory.EnumerateFiles(folder, "*.peg").Select(f => Path.GetFileName(f)).Order(StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            own = PegTextureSet.SearchOrder(Path.GetFileName(filePath), names, [""]).Select(n => new PegPack(n, () => File.ReadAllBytes(Path.Combine(folder!, n))));
        }
        else if (shared.Count == 0) return null;
        else own = [];
        var packs = own.ToList();
        var taken = new HashSet<string>(packs.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var p in shared) if (taken.Add(p.Name)) packs.Add(p);
        if (shared.Count > 0) label += " and the install's shared packs";
        return new PegTextureSet(label, packs);
    }

    /// <summary>
    /// The Red Faction II install a level belongs to: for an entry of a packfile group (<c>.toc_group</c>) its game
    /// folder, for a file on disk the nearest folder above it that holds <c>pc_media\Permanent_data.toc_group</c>; null
    /// when there is none.
    /// </summary>
    public static string? Rf2InstallOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            string full = Path.GetFullPath(path);
            string? folder = full.EndsWith(Rf2TocGroup.Extension, StringComparison.OrdinalIgnoreCase) ? Rf2TocGroup.GameFolderOf(full) : Path.GetDirectoryName(full);
            for (int i = 0; folder is not null && i < 4; i++, folder = Path.GetDirectoryName(folder))
                if (File.Exists(Path.Combine(folder, "pc_media", "Permanent_data" + Rf2TocGroup.Extension))) return folder;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>The non-empty shared packs of <paramref name="rf2Install"/> (empty when it has none). Any thread.</summary>
    public static IReadOnlyList<PegPack> SharedPacks(string rf2Install) => Shared.GetOrAdd(rf2Install, Rf2LevelTextures.PermanentPacks);

    /// <summary>The clutter classes of <paramref name="rf2Install"/>'s <c>clutter.tbl</c>, or null when it cannot be read. Any thread.</summary>
    public static IReadOnlyDictionary<string, Rf2ClutterClass>? ClutterClasses(string rf2Install) => Clutter.GetOrAdd(rf2Install, Rf2ClutterTable.FromGameFolder);

    /// <summary>
    /// <paramref name="rf2"/> with what a conversion of a Red Faction II level reads from beside it: its texture packs
    /// and, when its install is known, the install's clutter classes. Any thread.
    /// </summary>
    public static LvlRf2Options Complete(LvlRf2Options rf2, string levelName, string? filePath, IAssetSiblings? siblings, string? rf2Install)
    {
        var packs = rf2.TexturePacks ?? PacksFor(levelName, filePath, siblings, rf2Install);
        var clutter = rf2.ClutterClasses ?? (rf2Install is null ? null : ClutterClasses(rf2Install));
        return rf2 with { TexturePacks = packs, ClutterClasses = clutter };
    }
}
