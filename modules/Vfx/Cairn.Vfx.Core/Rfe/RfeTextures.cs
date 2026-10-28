using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using Cairn.Assets;
using Cairn.Formats;
using Cairn.Formats.Imaging;
using Cairn.Formats.Rf2;

namespace Cairn.Vfx.Rfe;

/// <summary>
/// Where a Red Faction II effect's textures are: in <c>.peg</c> packs, searched in the order the format notes found on the
/// stock files. First the effect's own <c>&lt;stem&gt;_vfx.peg</c>, then <c>&lt;stem&gt;.peg</c> (and the same for the stem
/// cut at its underscores); then the other packs of the same folder or packfile that hold one of its textures; then
/// <c>always_loaded.peg</c> of the install's Permanent_data group; then any other pack of the install that holds one.
/// The packs beyond the first two are found through an index of the packs' directories (only their first bytes are
/// read), built once per folder or packfile (known by its full path) and per install, and kept for the session.
/// </summary>
public static class RfeTextures
{
    /// <summary>The pack suffixes tried for the effect's stem, best first.</summary>
    public static IReadOnlyList<string> Suffixes { get; } = ["_vfx", ""];

    /// <summary>The group that holds <see cref="AlwaysLoaded"/>.</summary>
    public const string PermanentGroup = "Permanent_data";

    /// <summary>The pack most effect textures outside their own pack come from.</summary>
    public const string AlwaysLoaded = "always_loaded.peg";

    private static readonly ConcurrentDictionary<string, Lazy<Dictionary<string, string>>> s_groupIndexes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<(string Pack, string Group, Func<byte[]?> Read, IReadOnlySet<string> Stems)>>> s_installs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The packs for an effect's textures, in search order.
    /// </summary>
    /// <param name="effectName">The effect's file name.</param>
    /// <param name="textures">The texture names the effect uses.</param>
    /// <param name="siblingNames">The files beside the effect (its folder or its packfile).</param>
    /// <param name="read">Reads the first bytes (int.MaxValue: all) of one of them, or null when it is gone. Any thread.</param>
    /// <param name="label">Where the siblings are, for messages ("Botmatch_vfx.toc_group").</param>
    /// <param name="install">The Red Faction II game folder the effect belongs to, or null.</param>
    /// <param name="source">
    /// What the siblings are, to keep their texture index for the session: the full path of the folder or packfile. Null
    /// when it is not known (the index is then built for this call only): a label alone can name two different places.
    /// </param>
    public static PegTextureSet PacksFor(string effectName, IReadOnlyCollection<string> textures, IEnumerable<string> siblingNames,
        Func<string, int, byte[]?> read, string label, string? install, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(effectName);
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(siblingNames);
        ArgumentNullException.ThrowIfNull(read);
        var names = siblingNames.Where(n => n.EndsWith(".peg", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var packs = new List<PegPack>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 1-2: the effect's own packs
        foreach (string pack in PegTextureSet.SearchOrder(Path.GetFileName(effectName), names, Suffixes, maxOtherPacks: 0))
            if (taken.Add(pack)) packs.Add(new PegPack(pack, () => read(pack, int.MaxValue)));
        var wanted = textures.Where(t => !string.IsNullOrWhiteSpace(t)).Select(Stem).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var own = packs.Count == 0 ? null : new PegTextureSet(label, packs);
        var left = wanted.Where(s => own?.Contains(s) != true).ToList();
        // 3: the other packs of the folder or packfile that hold one of them
        if (left.Count > 0 && names.Count > taken.Count)
        {
            // kept per source (and its list of packs, so a pack added later is seen); never per label
            var index = string.IsNullOrEmpty(source) ? Index(names, read)
                : s_groupIndexes.GetOrAdd(FullPath(source) + "|" + string.Join("|", names),
                    _ => new Lazy<Dictionary<string, string>>(() => Index(names, read), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            foreach (string stem in left.ToList())
            {
                if (!index.TryGetValue(stem, out var pack)) continue;
                left.Remove(stem);
                if (taken.Add(pack)) packs.Add(new PegPack(pack, () => read(pack, int.MaxValue)));
            }
        }
        if (left.Count > 0 && install is not null)
        {
            var all = s_installs.GetOrAdd(Path.GetFullPath(install), path => new Lazy<IReadOnlyList<(string, string, Func<byte[]?>, IReadOnlySet<string>)>>(
                () => InstallPacks(path), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            // 4: always_loaded.peg, then 5: whichever other pack of the install holds one
            foreach (var (pack, group, readPack, stems) in all.OrderBy(p => p.Pack.Equals(AlwaysLoaded, StringComparison.OrdinalIgnoreCase) && p.Group.Equals(PermanentGroup, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                if (left.Count == 0) break;
                if (!left.Any(stems.Contains)) continue;
                left.RemoveAll(stems.Contains);
                string shown = $"{pack} ({group})";
                if (taken.Add(shown)) packs.Add(new PegPack(shown, readPack));
            }
        }
        return new PegTextureSet(label, packs);
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

    /// <summary>The texture stems of a <c>.peg</c> from its first bytes (its directory), or null when it is not one Cairn reads.</summary>
    public static IReadOnlySet<string>? PegStems(string name, Func<int, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            var head = read(32);
            if (head is null || head.Length < 20 || !PegCodec.IsPeg(head)) return null;
            byte[]? bytes = null;
            if (BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4)) == 6)
            {
                long directory = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
                if (directory <= 64L << 20) bytes = read(32 + (int)directory);
            }
            PegFile file;
            try { file = PegCodec.Read(bytes ?? read(int.MaxValue) ?? [], name); }
            catch (AssetFormatException) when (bytes is not null) { file = PegCodec.Read(read(int.MaxValue) ?? [], name); }
            return file.Textures.Select(t => Stem(t.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is AssetFormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static Dictionary<string, string> Index(IReadOnlyList<string> packs, Func<string, int, byte[]?> read)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pack in packs)
        {
            if (PegStems(pack, n => read(pack, n)) is not { } stems) continue;
            foreach (string stem in stems) index.TryAdd(stem, pack);
        }
        return index;
    }

    private static IReadOnlyList<(string, string, Func<byte[]?>, IReadOnlySet<string>)> InstallPacks(string install)
    {
        var list = new List<(string, string, Func<byte[]?>, IReadOnlySet<string>)>();
        string media = Path.Combine(install, "pc_media");
        IEnumerable<string> tocs;
        try { tocs = [.. Directory.EnumerateFiles(media, "*" + Rf2TocGroup.Extension).Order(StringComparer.OrdinalIgnoreCase)]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return list; }
        foreach (string toc in tocs)
        {
            Rf2GroupFiles group;
            try { group = new Rf2GroupFiles(toc); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException) { continue; }
            foreach (string pack in group.Names.Where(n => n.EndsWith(".peg", StringComparison.OrdinalIgnoreCase)))
            {
                if (PegStems(pack, n => group.Read(pack, n)) is { Count: > 0 } stems)
                    list.Add((pack, Path.GetFileNameWithoutExtension(toc), () => group.Read(pack, int.MaxValue), stems));
            }
        }
        return list;
    }

    /// <summary>A texture name's stem (its extension dropped when it is a texture extension).</summary>
    public static string Stem(string name)
    {
        string bare = Path.GetFileName((name ?? "").Trim());
        string ext = Path.GetExtension(bare);
        return ext.Length > 0 && AssetResolver.IsTextureName(bare) ? bare[..^ext.Length] : bare;
    }
}

/// <summary>The files of a Red Faction II packfile group, by name: a packed group's from its <c>.packfile</c>, a loose one's from disk.</summary>
public sealed class Rf2GroupFiles
{
    private readonly string _toc;
    private readonly string? _packfile;
    private readonly Dictionary<string, Rf2TocEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the group file at <paramref name="toc"/>.</summary>
    /// <exception cref="AssetFormatException">It is not a packfile group.</exception>
    /// <exception cref="IOException">It cannot be read.</exception>
    public Rf2GroupFiles(string toc)
    {
        _toc = toc;
        var group = Rf2TocGroup.Open(toc);
        _packfile = group.IsPacked ? group.FindPackfile(toc) : null;
        foreach (var e in group.Entries) _entries.TryAdd(e.Name, e);
        Names = [.. _entries.Keys];
        IsPacked = group.IsPacked;
    }

    /// <summary>The entry names.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>True for a packed group.</summary>
    public bool IsPacked { get; }

    /// <summary>The group file's name ("All_Levels.toc_group").</summary>
    public string Label => Path.GetFileName(_toc);

    /// <summary>The first <paramref name="limit"/> bytes of an entry (all of a shorter one), or null when there is none or it cannot be read. Any thread.</summary>
    public byte[]? Read(string name, int limit)
    {
        if (string.IsNullOrEmpty(name) || limit < 0 || !_entries.TryGetValue(Path.GetFileName(name), out var e)) return null;
        try
        {
            string? path = e.StoredPath is null ? _packfile : Rf2TocGroup.LooseFilePath(_toc, e);
            if (path is null) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (e.StoredPath is null)
            {
                if (e.Offset + e.Size > stream.Length) return null;
                stream.Position = e.Offset;
            }
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
