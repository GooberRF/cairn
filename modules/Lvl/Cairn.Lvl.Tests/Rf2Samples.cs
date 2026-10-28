using Cairn.Assets;
using Cairn.Formats;
using Cairn.Formats.Rf2;
using Cairn.Lvl.Model;
using Cairn.Workspace;

namespace Cairn.Lvl.Tests;

/// <summary>
/// Red Faction II levels through <see cref="LocalPaths"/>: REDUX's research copies (each beside its <c>.peg</c>) and
/// the levels inside a Red Faction II install's <c>All_Levels</c> packfile. Each source is empty when absent.
/// </summary>
internal static class Rf2Samples
{
    /// <summary>Levels that exercise the layout fixes (coronas without a bitmap, push regions of shape 1) and a small one.</summary>
    public static readonly string[] Bounded = ["dmc13.rfl", "l01s5.rfl", "l10s1c.rfl", "ctfo02.rfl"];

    public static string? ReduxFolder =>
        LocalPaths.ReduxResearchData is { } d && Directory.Exists(Path.Combine(d, "rf2_rfls")) ? Path.Combine(d, "rf2_rfls") : null;

    /// <summary>REDUX's RF2 levels.</summary>
    public static IReadOnlyList<LvlSamples.Sample> ReduxLevels() => [.. LvlSamples.Files(ReduxFolder, "*.rfl", recursive: false)];

    /// <summary>The packs beside a REDUX level (its own <c>&lt;level&gt;.peg</c> first).</summary>
    public static PegTextureSet ReduxPacks(string levelName) =>
        PegTextureSet.ForFolder($"beside {levelName}", Path.Combine(ReduxFolder ?? ".", levelName), [""]);

    /// <summary>The <c>All_Levels</c> group of the RF2 install, or null.</summary>
    public static (Rf2TocGroup Toc, string Packfile)? AllLevels() => Group("All_Levels");

    /// <summary>A packed group of the RF2 install with its packfile, or null.</summary>
    public static (Rf2TocGroup Toc, string Packfile)? Group(string name)
    {
        if (LocalPaths.Rf2GameDirectory is not { } dir) return null;
        string toc = Path.Combine(dir, "pc_media", name + Rf2TocGroup.Extension);
        if (!File.Exists(toc)) return null;
        try
        {
            var group = Rf2TocGroup.Open(toc);
            return group.IsPacked && group.FindPackfile(toc) is { } pack ? (group, pack) : null;
        }
        catch (Exception e) when (e is AssetFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>One entry's bytes out of a packed group.</summary>
    public static byte[] ReadEntry(string packfile, Rf2TocEntry entry)
    {
        using var f = File.OpenRead(packfile);
        f.Position = entry.Offset;
        var data = new byte[entry.Size];
        f.ReadExactly(data);
        return data;
    }

    /// <summary>The levels inside <c>All_Levels</c>.</summary>
    public static IReadOnlyList<LvlSamples.Sample> PackedLevels()
    {
        if (AllLevels() is not { } group) return [];
        var (toc, pack) = group;
        return [.. toc.Entries.Where(e => e.Name.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase))
            .Select(e => new LvlSamples.Sample("All_Levels/" + e.Name, () => ReadEntry(pack, e)))];
    }

    /// <summary>The packs of a group as <see cref="PegPack"/>s, the level's own pack first, then <c>Permanent_data</c>'s.</summary>
    public static PegTextureSet PackedPacks(string levelName)
    {
        var packs = new List<PegPack>();
        if (AllLevels() is { } levels)
        {
            var (toc, pack) = levels;
            string own = Path.GetFileNameWithoutExtension(levelName) + ".peg";
            foreach (var e in toc.Entries.Where(e => e.Name.Equals(own, StringComparison.OrdinalIgnoreCase)))
                packs.Add(new PegPack(e.Name, () => ReadEntry(pack, e)));
        }
        if (Group("Permanent_data") is { } permanent)
        {
            var (perm, permPack) = permanent;
            foreach (var e in perm.Entries.Where(e => e.Name.EndsWith(".peg", StringComparison.OrdinalIgnoreCase) && e.Size > 0))
                packs.Add(new PegPack(e.Name, () => ReadEntry(permPack, e)));
        }
        return new PegTextureSet($"for {levelName}", packs);
    }

    /// <summary>Everything about an RF2 level that is neither read exactly nor kept for a stated reason.</summary>
    public static List<string> Unexplained(RfLevel level)
    {
        var issues = new List<string>();
        foreach (var s in level.Sections)
        {
            bool fine = s.Status == RfSectionStatus.Opaque || s.Status == RfSectionStatus.Read && s.Detail is null or "empty";
            if (!fine) issues.Add($"{s.Name} at {s.Offset}: {s.Status} {s.Detail}");
        }
        if (level.IsTruncated) issues.Add("truncated");
        foreach (var p in level.Problems.Where(p => !p.Contains("no colour set", StringComparison.Ordinal))) issues.Add(p);
        return issues;
    }
}
