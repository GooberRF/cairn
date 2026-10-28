using Cairn.Assets;
using Cairn.Formats;
using Cairn.Formats.Rf2;

namespace Cairn.Lvl.Conversion;

/// <summary>
/// Finds the <c>.peg</c> texture packs of a Red Faction II level: its own <c>&lt;level&gt;.peg</c> first (every RF2
/// level has one beside it), then the packs of the install's <c>Permanent_data</c> group (<c>misc.peg</c> and the
/// others every level shares). Names compare case-insensitively; packs are read only when a texture is looked up.
/// </summary>
public static class Rf2LevelTextures
{
    /// <summary>The packs beside a level on disk (its folder's <c>.peg</c> files, its own first), plus <paramref name="shared"/> after them.</summary>
    /// <param name="levelPath">The level's full path.</param>
    /// <param name="shared">More packs to search last (for example <see cref="PermanentPacks"/>).</param>
    public static PegTextureSet ForFile(string levelPath, IEnumerable<PegPack>? shared = null)
    {
        ArgumentNullException.ThrowIfNull(levelPath);
        string? folder = Path.GetDirectoryName(Path.GetFullPath(levelPath));
        IEnumerable<string> names = [];
        try
        {
            if (folder is not null && Directory.Exists(folder))
                names = [.. Directory.EnumerateFiles(folder, "*.peg").Select(f => Path.GetFileName(f)).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            names = [];
        }
        var siblings = names.Select(n => new PegPack(n, () => File.ReadAllBytes(Path.Combine(folder!, n))));
        return ForLevel(Path.GetFileName(levelPath), siblings, shared);
    }

    /// <summary>
    /// The packs to search for a level's textures: among <paramref name="siblings"/> (the packs of its folder or
    /// packfile) its own pack, then the others when there are only a few (a whole packfile's hundreds are skipped), then
    /// <paramref name="shared"/>.
    /// </summary>
    public static PegTextureSet ForLevel(string levelName, IEnumerable<PegPack> siblings, IEnumerable<PegPack>? shared = null)
    {
        ArgumentNullException.ThrowIfNull(levelName);
        ArgumentNullException.ThrowIfNull(siblings);
        var byName = new Dictionary<string, PegPack>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in siblings) byName.TryAdd(p.Name, p);
        var order = PegTextureSet.SearchOrder(levelName, byName.Keys, [""]).Select(n => byName[n]).ToList();
        var taken = new HashSet<string>(order.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var p in shared ?? []) if (taken.Add(p.Name)) order.Add(p);
        return new PegTextureSet($"for {levelName}", order);
    }

    /// <summary>
    /// The non-empty <c>.peg</c> packs of a Red Faction II install's <c>Permanent_data</c> group, read from its packfile
    /// when asked for; empty when the install or the group is not there.
    /// </summary>
    /// <param name="rf2GameDirectory">The install folder (the one holding <c>pc_media</c>).</param>
    public static IReadOnlyList<PegPack> PermanentPacks(string rf2GameDirectory)
    {
        ArgumentNullException.ThrowIfNull(rf2GameDirectory);
        string toc = Path.Combine(rf2GameDirectory, "pc_media", "Permanent_data" + Rf2TocGroup.Extension);
        try
        {
            if (!File.Exists(toc)) return [];
            var group = Rf2TocGroup.Open(toc);
            var pegs = group.Entries.Where(e => e.Size > 0 && e.Name.EndsWith(".peg", StringComparison.OrdinalIgnoreCase)).ToList();
            if (group.IsPacked)
            {
                if (group.FindPackfile(toc) is not { } pack) return [];
                return [.. pegs.Select(e => new PegPack(e.Name, () => ReadRange(pack, e.Offset, e.Size)))];
            }
            return [.. pegs.Select(e => new PegPack(e.Name, () => Rf2TocGroup.LooseFilePath(toc, e) is { } path ? File.ReadAllBytes(path) : null))];
        }
        catch (Exception e) when (e is AssetFormatException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static byte[] ReadRange(string path, long offset, long size)
    {
        using var f = File.OpenRead(path);
        if (offset < 0 || size < 0 || offset + size > f.Length) throw new IOException($"{Path.GetFileName(path)} is shorter than its table of contents says.");
        f.Position = offset;
        var data = new byte[size];
        f.ReadExactly(data);
        return data;
    }
}
