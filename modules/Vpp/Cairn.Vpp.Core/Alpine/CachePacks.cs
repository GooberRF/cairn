using System.Globalization;
using Cairn.Formats;
using Cairn.Formats.Alpine;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Validation;

namespace Cairn.Vpp.Alpine;

/// <summary>What an Alpine Faction cache pack says, kept on its package.</summary>
/// <param name="Pack">The index as read.</param>
/// <param name="PackPath">The pack's full path.</param>
/// <param name="Typed">How many entries are listed with their content's extension added (dm01.rfl.jpg).</param>
/// <param name="Clashes">Listed names that are one file on Windows ("a.rfl / A.rfl"): the pack's names are case-sensitive.</param>
/// <param name="Corrupt">Indexes of the entries whose data does not match its CRC (the game does not use them).</param>
public sealed record CachePackInfo(CachePack Pack, string PackPath, int Typed, IReadOnlyList<string> Clashes, IReadOnlySet<int> Corrupt)
{
    /// <summary>The stored entry an item of this pack's package came from, or null.</summary>
    public CachePackEntry? EntryOf(VppItem item) =>
        item.OriginalIndex >= 0 && item.OriginalIndex < Pack.Entries.Count ? Pack.Entries[item.OriginalIndex] : null;

    /// <summary>The one-line description for the banner above the list.</summary>
    public string Banner
    {
        get
        {
            string count = Pack.Entries.Count == 1 ? "1 entry" : $"{Pack.Entries.Count:N0} entries";
            string text = $"Alpine Faction cache pack {Path.GetFileName(PackPath)} ({count}). It is read only in Cairn: preview, extract and convert its entries; nothing can be added, removed, renamed or saved.";
            if (Typed > 0) text += " Entries whose data does not match their name are listed with the data's type added (dm01.rfl.jpg); the details show the stored name.";
            return text;
        }
    }

    /// <summary>The pack's facts for the details pane: label and value pairs.</summary>
    public IReadOnlyList<(string Label, string Value)> Facts(IEnumerable<VppItem> items)
    {
        var c = CultureInfo.CurrentCulture;
        var rows = new List<(string, string)>
        {
            ("Format", string.Create(CultureInfo.InvariantCulture, $"Alpine Faction cache pack, version {CachePack.SupportedVersion}")),
            ("Write ID", string.Create(CultureInfo.InvariantCulture, $"0x{Pack.WriteId:X16} (changes on every save by the game)")),
            ("Entries", Pack.Entries.Count.ToString("N0", c)),
            ("Total size", string.Format(c, "{0:N0} bytes of data, {1:N0} bytes of header and index", Pack.DataBytes, Pack.DataStart)),
        };
        if (Typed > 0) rows.Add(("Listed with their type", string.Format(c, "{0:N0} (data that does not match its stored name)", Typed)));
        if (Corrupt.Count > 0) rows.Add(("Corrupt", string.Format(c, "{0:N0} (data that does not match its CRC: the game does not use it)", Corrupt.Count)));
        if (Clashes.Count > 0) rows.Add(("One file on Windows", string.Join(", ", Clashes.Take(4)) + (Clashes.Count > 4 ? $" and {Clashes.Count - 4:N0} more" : string.Empty)));
        rows.Add(("Cache pack", PackPath));
        foreach (var g in items.GroupBy(i => VppNames.ExtensionOf(i.Name), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            rows.Add(("Type " + (g.Key.Length == 0 ? "(none)" : g.Key), string.Format(c, "{0:N0} ({1:N0} bytes)", g.Count(), g.Sum(i => i.Size))));
        return rows;
    }
}

/// <summary>
/// Opens Alpine Faction cache packs (<c>.cache</c>) as read-only packages whose entries read from the pack. Nothing
/// here writes.
/// </summary>
public static class CachePacks
{
    /// <summary>True when the path names a cache pack by extension (other programs use .cache too: the reader checks the signature).</summary>
    public static bool IsCachePack(string? path) => CachePack.HasExtension(path);

    /// <summary>
    /// Opens a cache pack as a read-only package. Each entry is listed under its stored name, with its data's extension
    /// added when the data is of a known type its name does not say (Alpine's map thumbnails are JPEGs named after their
    /// levels, so <c>dm01.rfl</c> is listed as <c>dm01.rfl.jpg</c>).
    /// </summary>
    /// <exception cref="AssetFormatException">The file is not a cache pack Cairn reads, or is damaged.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static VppPackage Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        var stamp = VppArchiveStamp.Of(full);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
        var pack = CachePack.Read(stream, Path.GetFileName(full));
        var items = ImmutableArray.CreateBuilder<VppItem>(pack.Entries.Count);
        var head = new byte[16];
        var corrupt = new HashSet<int>();
        int typed = 0;
        for (int i = 0; i < pack.Entries.Count; i++)
        {
            var e = pack.Entries[i];
            if (e.Size > int.MaxValue) throw new AssetFormatException($"'{Path.GetFileName(full)}' holds {e.Name} with {e.Size:N0} bytes, more than Cairn reads from one entry.");
            string? actual = null;
            if (e.Size > 0)
            {
                stream.Position = e.Offset;
                int got = stream.ReadAtLeast(head.AsSpan(0, (int)Math.Min(head.Length, e.Size)), 1, throwOnEndOfStream: false);
                actual = VppFacts.SniffExtension(head.AsSpan(0, got));
            }
            if (!CachePack.DataMatches(stream, e)) corrupt.Add(i);
            string listed = ListedName(e.Name, actual);
            if (listed.Length != e.Name.Length) typed++;
            items.Add(new VppItem(listed, new ArchiveSource(full, e.Offset, (int)e.Size), VppItemState.Original) { OriginalName = listed, OriginalIndex = i });
        }
        var clashes = items.GroupBy(i => i.Name, VppNames.Comparer).Where(g => g.Count() > 1)
            .Select(g => string.Join(" / ", g.Select(i => i.Name))).ToList();
        int count = items.Count;
        return new VppPackage(full, items.MoveToImmutable(), stamp)
        {
            OriginalCount = count,
            Cache = new CachePackInfo(pack, full, typed, clashes, corrupt),
        };
    }

    /// <summary>The stored name, or the stored name plus <paramref name="actual"/> when the data is of a type its name does not say.</summary>
    internal static string ListedName(string stored, string? actual) =>
        actual is null || VppFacts.SameFamily(actual, VppNames.ExtensionOf(stored)) ? stored : stored + actual;

    /// <summary>Problem codes of cache packs (they replace the RF1 packfile rules, which do not apply).</summary>
    public static IReadOnlyDictionary<string, string> Rules { get; } = new Dictionary<string, string>
    {
        ["ACP001"] = "Names that are one file on Windows",
        ["ACP002"] = "Empty entry",
        ["ACP003"] = "Corrupt entry",
    };

    /// <summary>
    /// The facts worth flagging in a cache pack: entries whose data does not match its CRC (a warning: the game does not
    /// use them), and as information names that differ only in case (one file when extracted to one folder) and empty
    /// entries. The RF1 rules (name lengths, types the game loads) do not apply.
    /// </summary>
    public static IReadOnlyList<VppProblem> Validate(VppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Cache is not { } info) return [];
        var problems = new List<VppProblem>();
        foreach (var item in package.Items)
            if (info.Corrupt.Contains(item.OriginalIndex))
                problems.Add(new("ACP003", VppSeverity.Warning, $"{item.Name} is corrupt: its data does not match its CRC, so the game does not use it.", item.Name));
        foreach (var item in package.Items)
            if (item.Size == 0) problems.Add(new("ACP002", VppSeverity.Info, $"{item.Name} is an empty entry (0 bytes).", item.Name));
        foreach (var d in info.Clashes)
            problems.Add(new("ACP001", VppSeverity.Info, $"{d} differ only in case: on Windows they are one file, so extracting both into one folder writes it once."));
        return problems;
    }
}
