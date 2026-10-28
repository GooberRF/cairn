using Cairn.Formats;
using Cairn.Formats.Vpp;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;
using Cairn.Workspace;

namespace Cairn.Lvl.Tests;

/// <summary>
/// Real levels and groups through <see cref="LocalPaths"/> (each source is skipped when absent), and the rule a real
/// file is held to: every section read exactly, or kept for a stated reason.
/// </summary>
internal static class LvlSamples
{
    /// <summary>Set to 1 to run the opt-in corpus tests over every level and group under the game folder.</summary>
    public const string CorpusVariable = "CAIRN_LVL_CORPUS";

    public static bool CorpusEnabled => Environment.GetEnvironmentVariable(CorpusVariable) == "1";

    /// <summary>One sample: a display name and a way to get its bytes.</summary>
    public sealed record Sample(string Name, Func<byte[]> Load)
    {
        public override string ToString() => Name;
    }

    /// <summary>The stock levels inside the game folder's level packfiles (RF loads levels only from packfiles).</summary>
    public static IEnumerable<Sample> GameLevels(long byteBudget = long.MaxValue)
    {
        if (LocalPaths.GameDirectory is not { } dir) yield break;
        long used = 0;
        foreach (string pack in new[] { "levels1.vpp", "levels2.vpp", "levels3.vpp", "levelsm.vpp", "levelspf.vpp" })
        {
            string path = Path.Combine(dir, pack);
            if (!File.Exists(path)) continue;
            foreach (var s in PackEntries(path, ".rfl"))
            {
                if (used >= byteBudget) yield break;
                used += s.Size;
                yield return s.Sample;
            }
        }
    }

    /// <summary>Entries of one packfile with the given extension.</summary>
    public static IEnumerable<(Sample Sample, int Size)> PackEntries(string path, string extension)
    {
        VppArchive archive;
        try
        {
            archive = VppArchive.Open(path);
        }
        catch (Exception e) when (e is AssetFormatException or IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var entry in archive.Entries)
        {
            if (!entry.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
            var e = entry;
            yield return (new Sample($"{Path.GetFileName(path)}/{e.Name}", () =>
            {
                using var s = archive.OpenEntry(e);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }), e.Size);
        }
    }

    /// <summary>Loose files of one extension under a folder, in a stable order.</summary>
    public static IEnumerable<Sample> Files(string? root, string pattern, bool recursive, Func<string, bool>? keep = null)
    {
        if (root is null || !Directory.Exists(root)) return [];
        var files = Directory.EnumerateFiles(root, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => keep?.Invoke(f) ?? true)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return files.Select(f => new Sample(Path.GetRelativePath(root, f), () => File.ReadAllBytes(f)));
    }

    /// <summary>Every n-th item, at most <paramref name="max"/>, so a sample spans the whole list.</summary>
    public static IEnumerable<T> Spread<T>(IReadOnlyList<T> items, int max)
    {
        if (items.Count <= max) return items;
        double step = (double)items.Count / max;
        return Enumerable.Range(0, max).Select(i => items[(int)(i * step)]);
    }

    public static bool InRf2Folder(string path) =>
        path.Contains(Path.DirectorySeparatorChar + "rf2_rfls" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static bool IsRf2Level(byte[] data) => data.Length >= 8 && BitConverter.ToInt32(data, 4) == RflLevelReader.Rf2Version;

    /// <summary>Section details that are a stated reason, not a mismatch.</summary>
    private static bool AcceptedDetail(RfSectionRecord s) => s.Detail is null or "empty"
        || s.Detail.StartsWith("the editor views", StringComparison.Ordinal)
        || (s.Id == 0x0AFBA5ED && (s.Detail.StartsWith("ends before", StringComparison.Ordinal) || s.Detail.Contains("bytes after", StringComparison.Ordinal)));

    /// <summary>Everything about a level that is neither read exactly nor kept for a stated reason.</summary>
    public static List<string> Unexplained(RfLevel level)
    {
        var issues = new List<string>();
        foreach (var s in level.Sections)
        {
            switch (s.Status)
            {
                case RfSectionStatus.Read when AcceptedDetail(s):
                case RfSectionStatus.ReadWithFallback:
                case RfSectionStatus.Opaque:
                    break;
                default:
                    issues.Add($"{s.Name} at {s.Offset}: {s.Status} {s.Detail}");
                    break;
            }
        }
        if (level.IsTruncated) issues.Add("truncated");
        foreach (var p in level.Problems.Where(p => p.Contains("corners", StringComparison.Ordinal))) issues.Add(p);
        return issues;
    }

    /// <summary>Reads a level and returns its unexplained issues (a refusal counts as one).</summary>
    public static List<string> CheckLevel(byte[] data, string name)
    {
        try
        {
            var level = RflLevelReader.Read(data, name);
            CheckModel(level);
            return Unexplained(level);
        }
        catch (AssetFormatException e)
        {
            return [$"refused: {e.Message}"];
        }
    }

    /// <summary>Reads a group file and returns its unexplained issues.</summary>
    public static List<string> CheckGroup(byte[] data, string name)
    {
        try
        {
            var file = RfgReader.Read(data, name);
            var issues = new List<string>();
            if (file.UnknownDataOffset is { } at) issues.Add($"unknown data at {at}");
            foreach (var p in file.Problems.Where(p => p.Contains("corners", StringComparison.Ordinal) || p.Contains("does not match", StringComparison.Ordinal)))
                issues.Add(p);
            foreach (var g in file.Groups)
                foreach (var b in g.Brushes)
                    CheckGeometry(b.Geometry);
            return issues;
        }
        catch (AssetFormatException e)
        {
            return [$"refused: {e.Message}"];
        }
    }

    /// <summary>Sanity checks every reader result must pass whatever the file.</summary>
    public static void CheckModel(RfLevel level)
    {
        CheckGeometry(level.StaticGeometry);
        foreach (var b in level.Brushes) CheckGeometry(b.Geometry);
        foreach (var b in level.Movers) CheckGeometry(b.Geometry);
    }

    private static void CheckGeometry(RfGeometry g)
    {
        foreach (var f in g.Faces)
        {
            bool lit = f.HasSurface;
            foreach (var c in f.Corners)
                if (c.LightmapUv.HasValue != lit) throw new InvalidOperationException("lightmap UVs disagree with the surface rule");
        }
    }
}
